using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// The inner random stream that masks protected XML values. One keystream runs through the whole document:
/// every protected value, including those inside skipped subtrees, must be applied in document order.
/// </summary>
internal sealed class KdbxInnerStream
{
    private readonly ChaCha7539Engine m_engine = new();

    public KdbxInnerStream(ReadOnlySpan<byte> innerKey)
    {
        // ChaCha20 key and nonce are the first 32 and next 12 bytes of SHA-512(inner key).
        var hash = SHA512.HashData(innerKey);
        try
        {
            m_engine.Init(true, new ParametersWithIV(new KeyParameter(hash, 0, 32), hash, 32, 12));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public void Apply(Span<byte> value) => m_engine.ProcessBytes(value, value);
}
