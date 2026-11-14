using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// Payload encryption: AES-256-CBC with PKCS#7 padding (.NET) or raw RFC 7539 ChaCha20 with a 96-bit nonce
/// and counter 0 (BouncyCastle; .NET offers ChaCha20 only as the Poly1305 AEAD).
/// </summary>
internal static class KdbxOuterCipher
{
    public static byte[] Decrypt(bool chaCha20, byte[] key, byte[] iv, ReadOnlySpan<byte> ciphertext)
    {
        if (chaCha20)
        {
            return ChaCha20(key, iv, ciphertext);
        }
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
    }

    public static byte[] ChaCha20(byte[] key, byte[] iv, ReadOnlySpan<byte> input)
    {
        var engine = new ChaCha7539Engine();
        engine.Init(true, new ParametersWithIV(new KeyParameter(key), iv));
        var output = new byte[input.Length];
        engine.ProcessBytes(input, output);
        return output;
    }
}
