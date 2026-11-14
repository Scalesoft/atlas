using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>Key schedule of KDBX 4 for a password-only composite key.</summary>
internal static class KdbxKeys
{
    public const ulong HeaderBlockIndex = ulong.MaxValue;

    /// <summary>SHA-256 over the concatenated component hashes; the only component is SHA-256(UTF-8 password).</summary>
    public static byte[] Composite(string password)
    {
        var utf8 = Encoding.UTF8.GetBytes(password);
        var component = SHA256.HashData(utf8);
        try
        {
            return SHA256.HashData(component);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
            CryptographicOperations.ZeroMemory(component);
        }
    }

    public static byte[] EncryptionKey(ReadOnlySpan<byte> masterSeed, ReadOnlySpan<byte> transformedKey)
    {
        Span<byte> input = stackalloc byte[64];
        masterSeed.CopyTo(input);
        transformedKey.CopyTo(input[32..]);
        try
        {
            return SHA256.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    public static byte[] HmacBaseKey(ReadOnlySpan<byte> masterSeed, ReadOnlySpan<byte> transformedKey)
    {
        Span<byte> input = stackalloc byte[65];
        masterSeed.CopyTo(input);
        transformedKey.CopyTo(input[32..]);
        input[64] = 1;
        try
        {
            return SHA512.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    /// <summary>HMAC-SHA256 key of one block: SHA-512(block index as little-endian UInt64 || base key).</summary>
    public static byte[] BlockKey(ReadOnlySpan<byte> hmacBaseKey, ulong index)
    {
        Span<byte> input = stackalloc byte[8 + 64];
        BinaryPrimitives.WriteUInt64LittleEndian(input, index);
        hmacBaseKey.CopyTo(input[8..]);
        try
        {
            return SHA512.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    public static byte[] HeaderHmac(ReadOnlySpan<byte> hmacBaseKey, ReadOnlySpan<byte> header)
    {
        var key = BlockKey(hmacBaseKey, HeaderBlockIndex);
        try
        {
            return HMACSHA256.HashData(key, header);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>HMAC-SHA256 of a payload block: block index (UInt64), size (Int32), data, all little-endian.</summary>
    public static byte[] BlockHmac(ReadOnlySpan<byte> hmacBaseKey, ulong index, ReadOnlySpan<byte> data)
    {
        var key = BlockKey(hmacBaseKey, index);
        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            Span<byte> prefix = stackalloc byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(prefix, index);
            BinaryPrimitives.WriteInt32LittleEndian(prefix[8..], data.Length);
            hmac.AppendData(prefix);
            hmac.AppendData(data);
            return hmac.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
