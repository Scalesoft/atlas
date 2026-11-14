using System.Security.Cryptography;
using Atlas.Server.Features.Vaults.Kdbx;

namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>
/// In-memory KDBX composite keys of unlocked vaults (never the password string, never persisted), and next to each the
/// transformed key its last read derived, so a release with an unchanged header skips the key derivation. Both open the file
/// like the password and get the same protection: held only here, zeroed when the vault locks (master key changed, password
/// replaced, vault archived), when its key is replaced and at shutdown, never logged. Callers get copies and zero them after use.
/// </summary>
public sealed class VaultKeyring : IDisposable
{
    private readonly Lock m_gate = new();
    private readonly Dictionary<Guid, byte[]> m_keys = [];
    private readonly Dictionary<Guid, DerivedKey> m_derived = [];

    public bool Contains(Guid vaultId)
    {
        lock (m_gate)
        {
            return m_keys.ContainsKey(vaultId);
        }
    }

    /// <summary>Whether a transformed key of the vault is held; diagnostics and tests.</summary>
    public bool HasDerivedKey(Guid vaultId)
    {
        lock (m_gate)
        {
            return m_derived.ContainsKey(vaultId);
        }
    }

    /// <summary>A copy of the key, or null when the vault is locked. The caller zeroes the copy.</summary>
    public byte[]? Copy(Guid vaultId)
    {
        lock (m_gate)
        {
            return m_keys.TryGetValue(vaultId, out var key) ? key.ToArray() : null;
        }
    }

    /// <summary>Stores a copy of the key and zeroes the one it replaces, with the transformed key derived from it.</summary>
    public void Store(Guid vaultId, ReadOnlySpan<byte> compositeKey)
    {
        var copy = compositeKey.ToArray();
        lock (m_gate)
        {
            if (m_keys.Remove(vaultId, out var previous))
            {
                CryptographicOperations.ZeroMemory(previous);
            }
            ForgetDerived(vaultId);
            m_keys[vaultId] = copy;
        }
    }

    /// <summary>
    /// The transformed key cache of one vault for the KDBX reader. It hits only for the composite key the keyring holds now
    /// and the KDF parameters it was derived with, and remembers nothing for a vault that is locked or whose key changed meanwhile.
    /// </summary>
    public IKdbxDerivedKeyCache DerivedKeys(Guid vaultId) => new VaultDerivedKeys(this, vaultId);

    public void Lock(Guid vaultId)
    {
        lock (m_gate)
        {
            if (m_keys.Remove(vaultId, out var key))
            {
                CryptographicOperations.ZeroMemory(key);
            }
            ForgetDerived(vaultId);
        }
    }

    public void Dispose()
    {
        lock (m_gate)
        {
            foreach (var key in m_keys.Values)
            {
                CryptographicOperations.ZeroMemory(key);
            }
            m_keys.Clear();
            foreach (var derived in m_derived.Values)
            {
                CryptographicOperations.ZeroMemory(derived.TransformedKey);
            }
            m_derived.Clear();
        }
    }

    public override string ToString() => "Vault keyring";

    private byte[]? FindDerived(Guid vaultId, ReadOnlySpan<byte> compositeKey, ReadOnlySpan<byte> kdfIdentity)
    {
        lock (m_gate)
        {
            return m_keys.TryGetValue(vaultId, out var key) && CryptographicOperations.FixedTimeEquals(key, compositeKey)
                && m_derived.TryGetValue(vaultId, out var derived) && derived.KdfIdentity.AsSpan().SequenceEqual(kdfIdentity)
                    ? derived.TransformedKey.ToArray()
                    : null;
        }
    }

    private void RememberDerived(Guid vaultId, ReadOnlySpan<byte> compositeKey, ReadOnlySpan<byte> kdfIdentity, ReadOnlySpan<byte> transformedKey)
    {
        var derived = new DerivedKey(kdfIdentity.ToArray(), transformedKey.ToArray());
        lock (m_gate)
        {
            if (m_keys.TryGetValue(vaultId, out var key) && CryptographicOperations.FixedTimeEquals(key, compositeKey))
            {
                ForgetDerived(vaultId);
                m_derived[vaultId] = derived;
                return;
            }
        }
        CryptographicOperations.ZeroMemory(derived.TransformedKey);
    }

    private void ForgetDerived(Guid vaultId)
    {
        if (m_derived.Remove(vaultId, out var derived))
        {
            CryptographicOperations.ZeroMemory(derived.TransformedKey);
        }
    }

    /// <summary>KdfIdentity: SHA-256 of the public KDF parameters; TransformedKey: the secret KDF output.</summary>
    private sealed record DerivedKey(byte[] KdfIdentity, byte[] TransformedKey)
    {
        public override string ToString() => "Derived vault key";
    }

    private sealed class VaultDerivedKeys(VaultKeyring keyring, Guid vaultId) : IKdbxDerivedKeyCache
    {
        public byte[]? Find(ReadOnlySpan<byte> compositeKey, ReadOnlySpan<byte> kdfIdentity) => keyring.FindDerived(vaultId, compositeKey, kdfIdentity);

        public void Remember(ReadOnlySpan<byte> compositeKey, ReadOnlySpan<byte> kdfIdentity, ReadOnlySpan<byte> transformedKey) =>
            keyring.RememberDerived(vaultId, compositeKey, kdfIdentity, transformedKey);

        public override string ToString() => "Vault derived keys";
    }
}
