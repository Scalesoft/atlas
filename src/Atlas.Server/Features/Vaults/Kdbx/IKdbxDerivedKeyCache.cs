namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// Holder of the KDF output (transformed key) of one database, so repeated reads with an unchanged header skip the
/// derivation. A KDF identity names the key derivation parameters of the header (function, salt or seed, rounds, memory,
/// iterations, parallelism, version, secret and associated data); the composite key is the other half of the identity.
/// The transformed key opens the file like the password, so an implementation protects it like the composite key.
/// </summary>
public interface IKdbxDerivedKeyCache
{
    /// <summary>A copy of the transformed key for this composite key and these parameters, or null. The caller zeroes the copy.</summary>
    byte[]? Find(ReadOnlySpan<byte> compositeKey, ReadOnlySpan<byte> kdfIdentity);

    /// <summary>Keeps a copy of a transformed key the header HMAC just verified.</summary>
    void Remember(ReadOnlySpan<byte> compositeKey, ReadOnlySpan<byte> kdfIdentity, ReadOnlySpan<byte> transformedKey);
}
