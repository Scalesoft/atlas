using System.Security.Cryptography;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Kdbx;
using Atlas.Server.Features.Vaults.Queries;
using Atlas.Server.Features.Vaults.Runtime;
using Atlas.Server.Platform.Storage;
using Wolverine;

namespace Atlas.Server.Features.Vaults;

/// <summary>
/// The vault operations that touch files, each in three steps like the backup destination test: a message
/// authorizes and resolves the target, the file work (SMB, key derivation) runs outside any transaction, and a
/// command re-authorizes and writes. Only a successful commit installs the key in the keyring.
/// </summary>
public sealed class VaultOperations
{
    private readonly IMessageBus m_bus;
    private readonly VaultFiles m_files;
    private readonly VaultSnapshots m_snapshots;
    private readonly VaultRefreshService m_refresh;
    private readonly VaultProtection m_protection;
    private readonly ILogger<VaultOperations> m_logger;

    public VaultOperations(IMessageBus bus, VaultFiles files, VaultSnapshots snapshots, VaultRefreshService refresh, VaultProtection protection,
        ILogger<VaultOperations> logger)
    {
        m_bus = bus;
        m_files = files;
        m_snapshots = snapshots;
        m_refresh = refresh;
        m_protection = protection;
        m_logger = logger;
    }

    public async Task<VaultLocationTestResult> TestLocationAsync(TestVaultLocationRequest request, CancellationToken ct)
    {
        var storage = await m_bus.InvokeAsync<FileStorageOptions>(new PrepareLocationTest(request), ct);
        try
        {
            using var files = FileStorages.Open(storage);
            await files.TestAsync(ct);
        }
        catch (Exception error) when (VaultFailures.IsUnavailable(error))
        {
            // Storage messages are fixed guidance (server, share, permissions); they carry no credentials.
            throw new ApiException(400, "Test přístupu selhal: " + error.Message);
        }
        return new VaultLocationTestResult("Přístup ověřen: vytvoření, zápis, čtení, přejmenování a smazání testovacího souboru proběhlo úspěšně.");
    }

    /// <summary>Most files a location listing returns; a folder with more asks the user to narrow the search.</summary>
    public const int ListingLimit = 500;

    /// <summary>
    /// The *.kdbx files of a location's folder for picking the file to attach, optionally narrowed by a case-insensitive part of the
    /// name. Storage problems are the administrator's input errors (400) with fixed guidance, never server failures.
    /// </summary>
    public async Task<VaultLocationFiles> ListLocationFilesAsync(Guid locationId, Guid? spaceId, string? query, CancellationToken ct)
    {
        var term = query?.Trim() ?? "";
        if (term.Length > 200)
        {
            throw new ApiException(400, "Hledaný text smí mít nejvýše 200 znaků.");
        }
        var target = await m_bus.InvokeAsync<LocationListingTarget>(new PrepareLocationListing(locationId, spaceId), ct);
        FileStorageListing listing;
        try
        {
            using var storage = FileStorages.Open(target.Storage);
            // With a search term the folder is read whole and narrowed here; the bound applies to what is returned.
            listing = await storage.ListAsync(".kdbx", term.Length == 0 ? ListingLimit : int.MaxValue, ct);
        }
        catch (Exception error) when (VaultFailures.IsUnavailable(error))
        {
            throw new ApiException(400, VaultFailures.DescribeUnavailable(error), "vault_location_unavailable");
        }
        var matching = listing.Files.Where(file => term.Length == 0 || file.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        var files = matching.Take(ListingLimit)
            .Select(file => new VaultLocationFile(file.Name, file.Length, file.LastWriteUtc, target.Registered.ContainsKey(file.Name),
                target.Registered.GetValueOrDefault(file.Name)))
            .ToArray();
        return new VaultLocationFiles(files, listing.Truncated || matching.Count > ListingLimit);
    }

    /// <summary>A new empty database. A generated password is returned once; a custom one only when it has 12+ characters.</summary>
    public async Task<CreatedVault> CreateAsync(CreateVaultRequest request, CancellationToken ct)
    {
        var target = await m_bus.InvokeAsync<VaultTarget>(new PrepareNewVault(request.SpaceId, request.LocationId, request.Name, request.FileName), ct);
        var generated = request.GeneratePassword ? VaultPasswords.Generate() : null;
        var password = generated ?? VaultPasswords.RequireNew(request.Password);
        var key = KdbxReader.CompositeKey(password);
        try
        {
            var written = await Guard(async () =>
            {
                if (await m_files.ExistsAsync(target.Storage, target.FileName, ct))
                {
                    throw new ApiException(409, "Soubor s tímto názvem už v umístění existuje. Připojte ho jako existující trezor.", "vault_file_exists");
                }
                return await m_files.CreateAsync(target.Storage, target.FileName, password, request.Name.Trim(), ct);
            });
            try
            {
                // Byte-for-byte read-back of the stored copy; the writer already decrypted it once, so no second KDF.
                var stored = await Guard(() => m_files.ReadAsync(target.Storage, target.FileName, key, false, written, ct));
                var read = stored with { Entries = stored.Entries ?? [] };
                var view = await RegisterAsync(target, request.SpaceId, request.LocationId, request.Name, password, read, key, ct);
                return new CreatedVault(view, generated);
            }
            catch
            {
                await DeleteCreatedAsync(target);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<VaultView> AttachAsync(AttachVaultRequest request, CancellationToken ct)
    {
        var target = await m_bus.InvokeAsync<VaultTarget>(new PrepareNewVault(request.SpaceId, request.LocationId, request.Name, request.FileName), ct);
        var password = VaultPasswords.RequireExisting(request.Password);
        var key = KdbxReader.CompositeKey(password);
        try
        {
            var read = await Guard(() => m_files.ReadAsync(target.Storage, target.FileName, key, false, null, ct));
            return await RegisterAsync(target, request.SpaceId, request.LocationId, request.Name, password, read, key, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Re-enters the master password (after KeyChanged or a lost key ring) and unlocks the vault.</summary>
    public async Task<VaultView> ReplacePasswordAsync(Guid vaultId, VaultPasswordRequest request, CancellationToken ct)
    {
        var target = await m_bus.InvokeAsync<VaultTarget>(new PrepareVault(vaultId), ct);
        var password = VaultPasswords.RequireExisting(request.Password);
        var key = KdbxReader.CompositeKey(password);
        try
        {
            var read = await Guard(() => m_files.ReadAsync(target.Storage, target.FileName, key, false, null, ct));
            var view = await m_bus.InvokeAsync<VaultView>(new ReplaceVaultPassword(vaultId, m_protection.ProtectKdbxPassword(password), read), ct);
            await m_snapshots.AcceptAsync(vaultId, key, read, ct);
            return view;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task ArchiveAsync(Guid vaultId, CancellationToken ct)
    {
        await m_bus.InvokeAsync(new ArchiveVault(vaultId), ct);
        m_snapshots.Forget(vaultId);
    }

    /// <summary>Reads and decrypts the file now with the stored password and compares it with the registry.</summary>
    public async Task<VaultVerification> VerifyAsync(Guid vaultId, CancellationToken ct)
    {
        var target = await m_bus.InvokeAsync<VaultTarget>(new PrepareVault(vaultId), ct);
        var snapshot = await m_refresh.RefreshAsync(target, true, ct);
        return await m_bus.InvokeAsync<VaultVerification>(new DescribeVaultVerification(vaultId, snapshot), ct);
    }

    private async Task<VaultView> RegisterAsync(VaultTarget target, Guid spaceId, Guid locationId, string name, string password, VaultFileRead read,
        byte[] key, CancellationToken ct)
    {
        var view = await m_bus.InvokeAsync<VaultView>(new RegisterVault(target.VaultId, spaceId, locationId, name, target.FileName,
            m_protection.ProtectKdbxPassword(password), read), ct);
        await m_snapshots.AcceptAsync(target.VaultId, key, read, ct);
        return view;
    }

    private async Task DeleteCreatedAsync(VaultTarget target)
    {
        try
        {
            await m_files.DeleteAsync(target.Storage, target.FileName, CancellationToken.None);
        }
        catch (Exception error) when (VaultFailures.IsUnavailable(error))
        {
            m_logger.LogWarning("A vault file created for {VaultId} could not be removed after a failed registration ({ErrorType}).", target.VaultId, error.GetType().Name);
        }
    }

    /// <summary>File problems of an administrator action are that administrator's input errors (400), never server failures.</summary>
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (KdbxException error)
        {
            throw new ApiException(400, VaultFailures.Describe(error.Failure, false), "vault_file_" + error.Failure.ToString().ToLowerInvariant());
        }
        catch (InvalidDataException)
        {
            throw new ApiException(400, VaultFailures.LimitExceeded, "vault_file_limitexceeded");
        }
        catch (Exception error) when (VaultFailures.IsUnavailable(error))
        {
            throw new ApiException(400, VaultFailures.DescribeUnavailable(error), "vault_file_unavailable");
        }
    }
}
