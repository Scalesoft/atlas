using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using static Atlas.Server.Tests.Vaults.KdbxTestFiles;

namespace Atlas.Server.Tests.Vaults;

/// <summary>
/// Test-side KDBX 4 key schedule for the AES-KDF fixtures, written independently of the production code
/// from the format description. It opens the payload of a KeePass file, lets a test change the decrypted
/// inner header and XML, and seals it again with valid block HMACs, so hostile content that only a holder of
/// the master password could produce reaches the parser behind the authentication checks.
/// </summary>
internal static class KdbxTestSealer
{
    public static byte[] OpenPayload(byte[] file, string password)
    {
        var (header, encryptionKey, _) = Keys(file, password);
        var ciphertext = new MemoryStream();
        var position = header + 64;
        while (true)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(position + 32));
            if (size == 0)
            {
                break;
            }
            ciphertext.Write(file, position + 36, size);
            position += 36 + size;
        }
        var plain = Crypt(file, encryptionKey, ciphertext.ToArray(), false);
        if (!Compressed(file))
        {
            return plain;
        }
        using var gzip = new GZipStream(new MemoryStream(plain), CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    public static byte[] Reseal(byte[] file, string password, byte[] payload)
    {
        var (header, encryptionKey, hmacBase) = Keys(file, password);
        if (Compressed(file))
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true))
            {
                gzip.Write(payload);
            }
            payload = compressed.ToArray();
        }
        var ciphertext = Crypt(file, encryptionKey, payload, true);
        using var output = new MemoryStream();
        output.Write(file, 0, header + 64);
        ulong index = 0;
        var offset = 0;
        while (true)
        {
            var size = Math.Min(1024 * 1024, ciphertext.Length - offset);
            var block = ciphertext.AsSpan(offset, size);
            var key = SHA512.HashData([.. BitConverter.GetBytes(index), .. hmacBase]);
            byte[] authenticated = [.. BitConverter.GetBytes(index), .. BitConverter.GetBytes(size), .. block];
            output.Write(HMACSHA256.HashData(key, authenticated));
            output.Write(BitConverter.GetBytes(size));
            output.Write(block);
            if (size == 0)
            {
                return output.ToArray();
            }
            offset += size;
            index++;
        }
    }

    /// <summary>
    /// The same database with one KDF value replaced (a new AES-KDF seed, as KeePass writes on every save, or other rounds),
    /// sealed again with a valid header hash, header HMAC and block HMACs for the same password.
    /// </summary>
    public static byte[] WithKdf(byte[] file, string password, string key, byte[] value)
    {
        var payload = OpenPayload(file, password);
        var changed = WithKdfValue(file, key, value);
        var (header, _, hmacBase) = Keys(changed, password);
        var headerKey = SHA512.HashData([.. BitConverter.GetBytes(ulong.MaxValue), .. hmacBase]);
        HMACSHA256.HashData(headerKey, changed.AsSpan(0, header)).CopyTo(changed, header + 32);
        return Reseal(changed, password, payload);
    }

    /// <summary>Splits a decrypted payload into its inner header and XML parts.</summary>
    public static (byte[] InnerHeader, string Xml) Split(byte[] payload)
    {
        var position = 0;
        while (true)
        {
            var id = payload[position];
            var size = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(position + 1));
            position += 5 + size;
            if (id == 0)
            {
                return (payload[..position], Encoding.UTF8.GetString(payload, position, payload.Length - position));
            }
        }
    }

    public static byte[] Join(byte[] innerHeader, string xml) => [.. innerHeader, .. Encoding.UTF8.GetBytes(xml)];

    private static (int Header, byte[] EncryptionKey, byte[] HmacBase) Keys(byte[] file, string password)
    {
        var header = HeaderLength(file);
        var seed = HeaderField(file, 4);
        var dictionary = HeaderField(file, 11);
        var transformSeed = KdfValue(dictionary, "S");
        var rounds = BinaryPrimitives.ReadUInt64LittleEndian(KdfValue(dictionary, "R"));
        var key = SHA256.HashData(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        using (var aes = Aes.Create())
        {
            aes.Key = transformSeed;
            for (ulong i = 0; i < rounds; i++)
            {
                key = aes.EncryptEcb(key, PaddingMode.None);
            }
        }
        var transformed = SHA256.HashData(key);
        return (header, SHA256.HashData([.. seed, .. transformed]), SHA512.HashData([.. seed, .. transformed, 1]));
    }

    private static byte[] Crypt(byte[] file, byte[] key, byte[] data, bool encrypt)
    {
        var iv = HeaderField(file, 7);
        if (iv.Length == 12)
        {
            var engine = new ChaCha7539Engine();
            engine.Init(encrypt, new ParametersWithIV(new KeyParameter(key), iv));
            var output = new byte[data.Length];
            engine.ProcessBytes(data, 0, data.Length, output, 0);
            return output;
        }
        using var aes = Aes.Create();
        aes.Key = key;
        return encrypt ? aes.EncryptCbc(data, iv) : aes.DecryptCbc(data, iv);
    }

    private static bool Compressed(byte[] file) => BinaryPrimitives.ReadUInt32LittleEndian(HeaderField(file, 3)) == 1;

    private static byte[] KdfValue(byte[] dictionary, string wanted)
    {
        var position = 2;
        while (dictionary[position] != 0)
        {
            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(dictionary.AsSpan(position + 1));
            var name = Encoding.UTF8.GetString(dictionary, position + 5, keyLength);
            var valueLength = BinaryPrimitives.ReadInt32LittleEndian(dictionary.AsSpan(position + 5 + keyLength));
            if (name == wanted)
            {
                return dictionary.AsSpan(position + 9 + keyLength, valueLength).ToArray();
            }
            position += 9 + keyLength + valueLength;
        }
        throw new InvalidOperationException("KDF parameter not found.");
    }
}
