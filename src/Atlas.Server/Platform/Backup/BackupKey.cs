using System.Security.Cryptography;
using System.Text;

namespace Atlas.Server.Platform.Backup;

/// <summary>
/// The backup key: 32 random bytes the administrator keeps outside Atlas (shown once, as base64url). It encrypts the secure part
/// with AES-GCM and never depends on the host's Data Protection keys, which a new host would not have. Encryption is deterministic:
/// the nonce is an HMAC of the path and the plaintext, so an unchanged file keeps its bytes and Git stores only real changes. That
/// reveals only whether a file changed, which the commit shows anyway. The path is authenticated, so files cannot be swapped.
/// </summary>
public sealed class BackupKey
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] m_encryption;
    private readonly byte[] m_nonce;

    private BackupKey(byte[] secret)
    {
        Text = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        m_encryption = Derive(secret, "atlas-backup-v7 encryption", 32);
        m_nonce = Derive(secret, "atlas-backup-v7 nonce", 32);
        Id = Convert.ToHexStringLower(Derive(secret, "atlas-backup-v7 id", 8));
    }

    /// <summary>What the administrator stores: 43 characters of base64url.</summary>
    public string Text { get; }

    /// <summary>Names the key in the manifest and in the status without revealing it.</summary>
    public string Id { get; }

    public static BackupKey Generate() => new(RandomNumberGenerator.GetBytes(32));

    public static BackupKey Parse(string? text)
    {
        var value = (text ?? "").Trim().Replace('-', '+').Replace('_', '/');
        try
        {
            var bytes = Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '='));
            if (bytes.Length == 32)
            {
                return new BackupKey(bytes);
            }
        }
        catch (FormatException)
        {
        }
        throw new ApiException(400, "Klíč zálohy má 43 znaků (base64url), jak ho Atlas zobrazil při vytvoření.");
    }

    public byte[] Encrypt(string path, byte[] plaintext)
    {
        var aad = Encoding.UTF8.GetBytes(path);
        byte[] message = [.. aad, 0, .. plaintext];
        var nonce = HMACSHA256.HashData(m_nonce, message)[..NonceSize];
        var result = new byte[1 + NonceSize + TagSize + plaintext.Length];
        result[0] = FormatVersion;
        nonce.CopyTo(result, 1);
        using var aes = new AesGcm(m_encryption, TagSize);
        aes.Encrypt(nonce, plaintext, result.AsSpan(1 + NonceSize + TagSize), result.AsSpan(1 + NonceSize, TagSize), aad);
        return result;
    }

    public byte[] Decrypt(string path, byte[] data)
    {
        if (data.Length < 1 + NonceSize + TagSize || data[0] != FormatVersion)
        {
            throw new InvalidDataException("Šifrovaný soubor zálohy je poškozený: " + path);
        }
        var plaintext = new byte[data.Length - 1 - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(m_encryption, TagSize);
            aes.Decrypt(data.AsSpan(1, NonceSize), data.AsSpan(1 + NonceSize + TagSize), data.AsSpan(1 + NonceSize, TagSize), plaintext,
                Encoding.UTF8.GetBytes(path));
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("Šifrovaný soubor zálohy nejde tímto klíčem ověřit: " + path, ex);
        }
        return plaintext;
    }

    private static byte[] Derive(byte[] secret, string purpose, int length)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, length, [], Encoding.UTF8.GetBytes(purpose));
}
