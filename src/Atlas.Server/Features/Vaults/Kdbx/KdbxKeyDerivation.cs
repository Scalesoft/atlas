using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// Runs KDBX key derivation one at a time per process (single flight), so concurrent unlocks cannot multiply
/// the bounded Argon2 memory. Parameters arrive already checked against <see cref="VaultOptions"/>.
/// Argon2 comes from BouncyCastle; AES-KDF uses the .NET AES primitive.
/// </summary>
public sealed class KdbxKeyDerivation : IDisposable
{
    private const int AesKdfCancellationInterval = 1 << 16;

    private readonly SemaphoreSlim m_gate = new(1, 1);
    private long m_derivations;

    /// <summary>How many derivations this process ran; diagnostics and tests of the derived key cache.</summary>
    public long Derivations => Interlocked.Read(ref m_derivations);

    internal byte[] Transform(ReadOnlySpan<byte> compositeKey, KdbxKdfParameters parameters, CancellationToken cancellationToken)
    {
        var key = compositeKey.ToArray();
        try
        {
            m_gate.Wait(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref m_derivations);
                return parameters.Kind == KdbxKdfKind.AesKdf
                    ? AesKdf(key, parameters, cancellationToken)
                    : Argon2(key, parameters);
            }
            finally
            {
                m_gate.Release();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Dispose() => m_gate.Dispose();

    private static byte[] AesKdf(byte[] key, KdbxKdfParameters parameters, CancellationToken cancellationToken)
    {
        using var aes = Aes.Create();
        aes.Key = parameters.Salt;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        // Both 16-byte halves are encrypted independently in ECB mode, then hashed once.
        for (ulong round = 0; round < parameters.Rounds; round++)
        {
            if (round % AesKdfCancellationInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            encryptor.TransformBlock(key, 0, key.Length, key, 0);
        }
        return SHA256.HashData(key);
    }

    private static byte[] Argon2(byte[] key, KdbxKdfParameters parameters)
    {
        var builder = new Argon2Parameters.Builder(parameters.Kind == KdbxKdfKind.Argon2d ? Argon2Parameters.Argon2d : Argon2Parameters.Argon2id)
            .WithVersion((int)parameters.Version)
            .WithIterations(checked((int)parameters.Iterations))
            .WithMemoryAsKB(checked((int)(parameters.MemoryBytes / 1024)))
            .WithParallelism((int)parameters.Parallelism)
            .WithSalt(parameters.Salt);
        if (parameters.SecretKey is { Length: > 0 } secret)
        {
            builder.WithSecret(secret);
        }
        if (parameters.AssociatedData is { Length: > 0 } associated)
        {
            builder.WithAdditional(associated);
        }
        var generator = new Argon2BytesGenerator();
        generator.Init(builder.Build());
        var output = new byte[32];
        generator.GenerateBytes(key, output);
        return output;
    }
}
