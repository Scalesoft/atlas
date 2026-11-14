using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Features.Backups;
using Atlas.Server.Platform.Storage;
using Microsoft.AspNetCore.DataProtection;
using NHibernate;
using NHibernate.Linq;
using Npgsql;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.StorageProfiles;

public sealed class StorageProfileService(AtlasDatabase database, IDataProtectionProvider protection, IWebHostEnvironment environment)
{
    private readonly IDataProtector m_passwords = protection.CreateProtector(BackupService.DestinationPasswordPurpose);

    public async Task<IReadOnlyList<StorageProfileView>> ListAsync(CancellationToken ct)
    {
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        var profiles = await session.Query<StorageProfile>().OrderBy(x => x.Name).Take(1000).ToListAsync(ct);
        var grants = await session.Query<StorageProfileSpace>().ToListAsync(ct);
        await tx.CommitAsync(ct);
        return profiles.Select(profile => View(profile, grants.Where(x => x.StorageProfileId == profile.Id).Select(x => x.SpaceId).ToArray())).ToArray();
    }

    public async Task<StorageProfileView> GetAsync(Guid id, CancellationToken ct)
    {
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        var profile = await session.GetAsync<StorageProfile>(id, ct) ?? throw new ApiException(404, "Úložiště nebylo nalezeno.");
        var spaces = await session.Query<StorageProfileSpace>().Where(x => x.StorageProfileId == id).Select(x => x.SpaceId).ToListAsync(ct);
        await tx.CommitAsync(ct);
        return View(profile, spaces.ToArray());
    }

    /// <param name="approval">For a profile the ZIP backups use: the destination change a fresh sign-in approved, audited with this
    /// save (SEC3-3).</param>
    public async Task<StorageProfileView> SaveAsync(Guid? id, StorageProfileRequest request, CancellationToken ct,
        Backups.Commands.BackupDestinationApproval? approval = null)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200 || request.Username is null || request.Domain is null || request.SpaceIds is null)
            throw new ApiException(400, "Zadejte název a platné nastavení úložiště.");
        var destination = StorageProfilePaths.NormalizeDestination(request.Destination);
        RequirePrivateDestination(destination);
        var spaces = request.AllSpaces ? [] : request.SpaceIds.Distinct().ToArray();
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        await new AtlasLocks(session).AcquireAdvisoryAsync(731145101, ct);
        var profile = id is { } existingId
            ? await session.GetAsync<StorageProfile>(existingId, LockMode.Upgrade, ct) ?? throw new ApiException(404, "Úložiště nebylo nalezeno.")
            : new StorageProfile();
        if (id.HasValue && profile.Version != request.ExpectedVersion) throw Conflict();
        if (profile.ProtectedPassword is not null && string.IsNullOrEmpty(request.Password))
            StoredCredential.Require(profile.Destination, profile.Username, profile.Domain, destination, request.Username.Trim(), request.Domain.Trim());
        var identity = StorageProfilePaths.CanonicalRoot(destination);
        var existing = await session.Query<StorageProfile>().Where(x => x.Id != profile.Id).Select(x => new { x.Name, x.Destination }).ToListAsync(ct);
        if (existing.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
            || StorageProfilePaths.Overlaps(x.Destination, identity)))
            throw new ApiException(409, "Úložiště s tímto názvem nebo překrývajícím se cílovým umístěním již existuje.");
        if (spaces.Length != 0 && await LiveSpace.Query(session).WhereRestrictionOn(x => x.Id).IsIn(spaces).RowCountAsync(ct) != spaces.Length)
            throw new ApiException(400, "Některý vybraný prostor již neexistuje.");
        var grants = await session.Query<StorageProfileSpace>().Where(x => x.StorageProfileId == profile.Id).ToListAsync(ct);
        profile.Name = name;
        profile.Destination = destination;
        profile.Username = request.Username.Trim();
        profile.Domain = request.Domain.Trim();
        profile.Enabled = request.Enabled;
        profile.AllowPageExports = request.AllowPageExports;
        profile.AllowBackups = request.AllowBackups;
        profile.AllSpaces = request.AllSpaces;
        if (!destination.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)) profile.ProtectedPassword = null;
        else if (!string.IsNullOrEmpty(request.Password)) profile.ProtectedPassword = m_passwords.Protect(request.Password);
        if (id.HasValue) profile.Version++;
        else await session.SaveAsync(profile, ct);
        foreach (var grant in grants.Where(x => !spaces.Contains(x.SpaceId))) await session.DeleteAsync(grant, ct);
        foreach (var spaceId in spaces.Where(spaceId => grants.All(x => x.SpaceId != spaceId)))
            await session.SaveAsync(new StorageProfileSpace { StorageProfileId = profile.Id, SpaceId = spaceId }, ct);
        if (approval is not null) await approval.WriteAsync(session, ct);
        try { await tx.CommitAsync(ct); }
        catch (Exception ex) when (IsConstraintConflict(ex))
        { throw new ApiException(409, "Úložiště s tímto názvem nebo překrývajícím se cílovým umístěním již existuje."); }
        return View(profile, spaces);
    }

    public async Task DeleteAsync(Guid id, int? expectedVersion, CancellationToken ct)
    {
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        await new AtlasLocks(session).AcquireAdvisoryAsync(731145101, ct);
        var profile = await session.GetAsync<StorageProfile>(id, LockMode.Upgrade, ct) ?? throw new ApiException(404, "Úložiště nebylo nalezeno.");
        if (profile.Version != expectedVersion) throw Conflict();
        if (await session.Query<BackupSettings>().AnyAsync(x => x.StorageProfileId == id, ct))
            throw new ApiException(409, "Úložiště používají zálohy nebo exporty stránek. Nejprve změňte jejich nastavení.");
        await session.DeleteAsync(profile, ct);
        try { await tx.CommitAsync(ct); }
        catch (Exception ex) when (IsConstraintConflict(ex))
        { throw new ApiException(409, "Úložiště používají zálohy nebo exporty stránek. Nejprve změňte jejich nastavení."); }
    }

    public async Task<IReadOnlyList<AvailableStorageProfile>> ListAvailableAsync(Guid spaceId, CancellationToken ct)
    {
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        var profiles = await session.Query<StorageProfile>().Where(x => x.Enabled && x.AllowPageExports &&
            (x.AllSpaces || session.Query<StorageProfileSpace>().Any(grant => grant.StorageProfileId == x.Id && grant.SpaceId == spaceId)))
            .OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Destination }).Take(1000).ToListAsync(ct);
        await tx.CommitAsync(ct);
        return profiles.Select(x => new AvailableStorageProfile(x.Id, x.Name, StorageProfilePaths.Display(x.Destination, ""))).ToArray();
    }

    public async Task<StorageProfile> RequireAvailableAsync(ISession session, Guid id, Guid spaceId, CancellationToken ct)
    {
        var profile = await session.GetAsync<StorageProfile>(id, ct) ?? throw new ApiException(404, "Úložiště nebylo nalezeno.");
        if (!profile.Enabled || !profile.AllowPageExports || !profile.AllSpaces &&
            !await session.Query<StorageProfileSpace>().AnyAsync(x => x.StorageProfileId == id && x.SpaceId == spaceId, ct))
            throw new ApiException(403, "Úložiště není povolené pro export v tomto prostoru.");
        return profile;
    }

    public Task<IFileStorage> OpenAsync(Guid id, string folder, bool forBackups, CancellationToken ct)
        => OpenVersionAsync(id, folder, forBackups, null, true, ct);

    public Task<IFileStorage> OpenAsync(Guid id, string folder, bool forBackups, int expectedVersion, CancellationToken ct)
        => OpenVersionAsync(id, folder, forBackups, expectedVersion, true, ct);

    /// <summary>A page-export folder that must already exist: browsing and checking a target never create folders.</summary>
    public Task<IFileStorage> OpenExistingAsync(Guid id, string folder, CancellationToken ct)
        => OpenVersionAsync(id, folder, false, null, false, ct);

    /// <summary>Creates <paramref name="name"/> in an existing page-export folder and returns the new folder's relative path.</summary>
    public async Task<string> CreateFolderAsync(Guid id, string parent, string name, CancellationToken ct)
    {
        name = name?.Trim() ?? "";
        try { FileStorages.ValidateName(name); }
        catch (ArgumentException) { throw new ApiException(400, "Zadejte název složky bez lomítek a znaků < > : \" | ? *."); }
        var folder = StorageProfilePaths.NormalizeFolder(parent.Length == 0 ? name : parent + "/" + name);
        using (var existing = await OpenExistingAsync(id, parent, ct))
        {
            if ((await existing.ListFoldersAsync(int.MaxValue, ct)).Files.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                || await existing.ExistsAsync(name, ct))
                throw new ApiException(409, "Položka s tímto názvem již ve složce existuje.");
        }
        // SMB creates the missing folder on open; a local folder is created here, after the link checks of the open.
        using var created = await OpenVersionAsync(id, folder, false, null, true, ct);
        var location = await LocalLocationAsync(id, folder, ct);
        if (location is not null) Directory.CreateDirectory(location);
        return folder;
    }

    private async Task<string?> LocalLocationAsync(Guid id, string folder, CancellationToken ct)
    {
        using var session = database.OpenSession();
        var root = (await session.GetAsync<StorageProfile>(id, ct))?.Destination ?? throw new ApiException(404, "Úložiště nebylo nalezeno.");
        return root.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) ? null : StorageProfilePaths.Destination(root, folder);
    }

    private async Task<IFileStorage> OpenVersionAsync(Guid id, string folder, bool forBackups, int? expectedVersion, bool create, CancellationToken ct)
    {
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        var profile = await session.GetAsync<StorageProfile>(id, ct) ?? throw new ApiException(404, "Úložiště nebylo nalezeno.");
        if (expectedVersion.HasValue && profile.Version != expectedVersion) throw Conflict();
        if (!profile.Enabled || (forBackups ? !profile.AllowBackups : !profile.AllowPageExports))
            throw new ApiException(403, "Úložiště je vypnuté nebo není povolené pro tento způsob použití.");
        await tx.CommitAsync(ct);
        return await OpenAsync(profile, folder, forBackups, create, ct);
    }
    public async Task TestAsync(Guid id, CancellationToken ct)
    {
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        var profile = await session.GetAsync<StorageProfile>(id, ct) ?? throw new ApiException(404, "Úložiště nebylo nalezeno.");
        await tx.CommitAsync(ct);
        using var storage = await OpenAsync(profile, "", false, true, ct);
        await storage.TestAsync(ct);
    }

    private async Task<IFileStorage> OpenAsync(StorageProfile profile, string folder, bool forBackups, bool create, CancellationToken ct)
    {
        var location = StorageProfilePaths.Destination(profile.Destination, folder);
        RequirePrivateDestination(location);
        if (!location.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)) StorageProfilePaths.RequireLocalContainment(profile.Destination, location);
        var options = new FileStorageOptions(location, profile.Username, profile.Domain,
            profile.ProtectedPassword is null ? null : m_passwords.Unprotect(profile.ProtectedPassword));
        if (location.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            await SmbProfileFolders.EnsureAsync(options, profile.Destination, create, ct);
        var storage = FileStorages.Open(options);
        return new ProfileFileStorage(storage, profile.Destination, location.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) ? null : location,
            token => ValidateMutationAsync(profile.Id, profile.Version, forBackups, token));
    }

    private async Task ValidateMutationAsync(Guid id, int version, bool forBackups, CancellationToken ct)
    {
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        var current = await session.GetAsync<StorageProfile>(id, ct);
        if (current is null || current.Version != version || !current.Enabled || (forBackups ? !current.AllowBackups : !current.AllowPageExports))
            throw Conflict();
        await tx.CommitAsync(ct);
    }

    private void RequirePrivateDestination(string destination)
    {
        if (destination.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)) return;
        var web = StorageProfilePaths.RealPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
        if (StorageProfilePaths.IsWithin(StorageProfilePaths.RealPath(destination), web))
            throw new ApiException(400, "Úložiště nesmí být ve veřejné složce wwwroot.");
    }

    private static StorageProfileView View(StorageProfile p, Guid[] spaces) =>
        new(p.Id, p.Name, p.Destination, p.Username, p.Domain, p.ProtectedPassword is not null,
            p.Enabled, p.AllowPageExports, p.AllowBackups, p.AllSpaces, spaces, p.Version);
    private static ApiException Conflict() => new(409, "Nastavení úložiště se změnilo. Načtěte aktuální hodnoty.");
    private static bool IsConstraintConflict(Exception ex) => ex is PostgresException { SqlState: "23503" or "23505" }
        || ex.InnerException is not null && IsConstraintConflict(ex.InnerException);
}
