using System.Collections.Concurrent;
using System.Security.Cryptography;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Kdbx;

namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>
/// Current metadata snapshot of every vault this process has touched, and the state machine behind it
/// (<see cref="VaultRuntimeState"/>). A refresh first compares file metadata (length, last write, change time,
/// file id) and reads nothing when it is unchanged; a changed file is read and hashed, and only different bytes
/// are decrypted. Key derivation (single flight across the process) runs only when the header's KDF parameters changed:
/// the keyring keeps the transformed key of each unlocked vault, while every read still verifies the current bytes.
/// A vault without a key in <see cref="VaultKeyring"/> unlocks automatically from its stored password.
/// Unreachable storage turns the vault Unavailable and hides its entries; a stored password that stops working
/// turns it KeyChanged and drops the key. Snapshots never contain secret values.
/// </summary>
public sealed class VaultSnapshots
{
    private readonly ConcurrentDictionary<Guid, VaultSlot> m_slots = new();
    private readonly VaultKeyring m_keyring;
    private readonly VaultFiles m_files;
    private readonly VaultProtection m_protection;
    private readonly ILogger<VaultSnapshots> m_logger;

    public VaultSnapshots(VaultKeyring keyring, VaultFiles files, VaultProtection protection, ILogger<VaultSnapshots> logger)
    {
        m_keyring = keyring;
        m_files = files;
        m_protection = protection;
        m_logger = logger;
    }

    public VaultSnapshot? Current(Guid vaultId) => m_slots.TryGetValue(vaultId, out var slot) ? slot.Current : null;

    /// <summary>True when <paramref name="stateVersion"/> is still the latest state of the vault.</summary>
    public bool IsCurrent(Guid vaultId, long stateVersion) => Current(vaultId)?.StateVersion == stateVersion;

    /// <summary>Checks the file and reloads it when it changed. <paramref name="force"/> reads and decrypts it regardless.</summary>
    public async Task<VaultSnapshot> RefreshAsync(VaultTarget target, bool force, CancellationToken ct)
    {
        var slot = m_slots.GetOrAdd(target.VaultId, _ => new VaultSlot());
        await slot.Gate.WaitAsync(ct);
        try
        {
            return await RefreshLockedAsync(slot, target, force, ct);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>
    /// Installs a key and snapshot an administrator just verified (new vault, attached file, re-entered password).
    /// Stores a copy of the key; the caller still zeroes its own.
    /// </summary>
    public async Task<VaultSnapshot> AcceptAsync(Guid vaultId, byte[] compositeKey, VaultFileRead read, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(read.Entries);
        var slot = m_slots.GetOrAdd(vaultId, _ => new VaultSlot());
        await slot.Gate.WaitAsync(ct);
        try
        {
            m_keyring.Store(vaultId, compositeKey);
            var snapshot = slot.Publish(vaultId, VaultRuntimeState.Fresh, read.Entries, read.Sha256, null, null);
            slot.Metadata = read.Metadata;
            return snapshot;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>
    /// Fields of one entry, read from the file on demand for a release (WP4 V5). Under the vault's gate it first checks the file
    /// like a refresh (automatic unlock, metadata check, reload when changed), so a locked, unavailable or broken vault answers its
    /// state and never a previous copy; then it reads and decrypts the file once more and copies only those fields. Nothing is
    /// cached: the caller zeroes <see cref="VaultFieldRead.Values"/>. Fresh without values means the entry is no longer in the file.
    /// </summary>
    public async Task<VaultFieldRead> ReadFieldsAsync(VaultTarget target, Guid entryUuid, IReadOnlyCollection<VaultSecretFields> fields, CancellationToken ct)
    {
        var slot = m_slots.GetOrAdd(target.VaultId, _ => new VaultSlot());
        await slot.Gate.WaitAsync(ct);
        try
        {
            var snapshot = await RefreshLockedAsync(slot, target, false, ct);
            if (snapshot.State != VaultRuntimeState.Fresh)
            {
                return new VaultFieldRead(snapshot.State, null);
            }
            var key = m_keyring.Copy(target.VaultId);
            if (key is null)
            {
                return new VaultFieldRead(VaultRuntimeState.Locked, null);
            }
            try
            {
                return new VaultFieldRead(VaultRuntimeState.Fresh,
                    await m_files.ReadFieldsAsync(target.Storage, target.FileName, key, entryUuid, fields, ct, m_keyring.DerivedKeys(target.VaultId)));
            }
            catch (KdbxException error)
            {
                // The next refresh publishes the new state; this release only fails.
                m_logger.LogWarning("Vault {VaultId} value read failed ({Failure}).", target.VaultId, error.Failure);
                return new VaultFieldRead(error.Failure is KdbxFailure.KeyChanged or KdbxFailure.WrongKey ? VaultRuntimeState.KeyChanged : VaultRuntimeState.Error, null);
            }
            catch (Exception error) when (error is InvalidDataException || VaultFailures.IsUnavailable(error))
            {
                m_logger.LogWarning("Vault {VaultId} value read failed ({ErrorType}).", target.VaultId, error.GetType().Name);
                return new VaultFieldRead(VaultRuntimeState.Unavailable, null);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Drops the key and the snapshot of an archived vault.</summary>
    public void Forget(Guid vaultId)
    {
        m_keyring.Lock(vaultId);
        m_slots.TryRemove(vaultId, out _);
    }

    private async Task<VaultSnapshot> RefreshLockedAsync(VaultSlot slot, VaultTarget target, bool force, CancellationToken ct)
    {
        var vaultId = target.VaultId;
        var key = m_keyring.Copy(vaultId);
        var unlocking = key is null;
        if (key is null)
        {
            var password = m_protection.UnprotectKdbxPassword(target.ProtectedKdbxPassword);
            if (password is null)
            {
                return slot.Publish(vaultId, VaultRuntimeState.Locked, null, null, null, VaultFailures.NoStoredPassword);
            }
            key = KdbxReader.CompositeKey(password);
        }
        try
        {
            var lastGood = force || unlocking ? null : slot.LastGood;
            if (lastGood is not null && slot.Current?.State == VaultRuntimeState.Fresh && slot.Metadata is { } known)
            {
                var metadata = await m_files.GetMetadataAsync(target.Storage, target.FileName, ct);
                if (metadata == known)
                {
                    return slot.Publish(vaultId, VaultRuntimeState.Fresh, lastGood.Entries, lastGood.Sha256, null, null);
                }
            }
            var read = await m_files.ReadAsync(target.Storage, target.FileName, key, true, lastGood?.Sha256, ct, m_keyring.DerivedKeys(vaultId));
            if (unlocking)
            {
                m_keyring.Store(vaultId, key);
            }
            var snapshot = slot.Publish(vaultId, VaultRuntimeState.Fresh, read.Entries ?? lastGood!.Entries, read.Sha256, null, null);
            slot.Metadata = read.Metadata;
            return snapshot;
        }
        catch (KdbxException error) when (error.Failure is KdbxFailure.KeyChanged or KdbxFailure.WrongKey)
        {
            m_keyring.Lock(vaultId);
            m_logger.LogWarning("Vault {VaultId} locked: the stored master password no longer opens the file.", vaultId);
            return slot.Publish(vaultId, VaultRuntimeState.KeyChanged, null, null, KdbxFailure.KeyChanged, VaultFailures.KeyChanged);
        }
        catch (KdbxException error)
        {
            m_logger.LogWarning("Vault {VaultId} cannot be read ({Failure}).", vaultId, error.Failure);
            return slot.Publish(vaultId, VaultRuntimeState.Error, null, null, error.Failure, VaultFailures.Describe(error.Failure, true));
        }
        catch (InvalidDataException)
        {
            // The storage refused to read past VaultOptions.MaxFileBytes.
            m_logger.LogWarning("Vault {VaultId} exceeds the permitted file size.", vaultId);
            return slot.Publish(vaultId, VaultRuntimeState.Error, null, null, KdbxFailure.LimitExceeded, VaultFailures.LimitExceeded);
        }
        catch (Exception error) when (VaultFailures.IsUnavailable(error))
        {
            m_logger.LogWarning("Vault {VaultId} is unavailable ({ErrorType}).", vaultId, error.GetType().Name);
            return slot.Publish(vaultId, VaultRuntimeState.Unavailable, null, null, null, VaultFailures.DescribeUnavailable(error));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
