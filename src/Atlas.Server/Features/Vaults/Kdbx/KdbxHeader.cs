using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// The unencrypted outer header of a KDBX 4 file. <see cref="Parse"/> bounds every field, verifies the
/// header SHA-256 and only then interprets fields, so a damaged header reports <see cref="KdbxFailure.Corrupt"/>
/// rather than a misleading unsupported algorithm. Everything here is cheap; no key derivation runs.
/// </summary>
internal sealed class KdbxHeader
{
    private const int FixedPrefixBytes = 12;
    private const int FieldPrefixBytes = 5;

    private KdbxHeader(int length, bool chaCha20, bool compressed, byte[] masterSeed, byte[] encryptionIv, KdbxKdfParameters kdf)
    {
        Length = length;
        ChaCha20 = chaCha20;
        Compressed = compressed;
        MasterSeed = masterSeed;
        EncryptionIv = encryptionIv;
        Kdf = kdf;
    }

    /// <summary>Header bytes covered by the SHA-256 and HMAC that follow it.</summary>
    public int Length { get; }

    /// <summary>True for ChaCha20, false for AES-256-CBC.</summary>
    public bool ChaCha20 { get; }

    public bool Compressed { get; }

    public byte[] MasterSeed { get; }

    public byte[] EncryptionIv { get; }

    public KdbxKdfParameters Kdf { get; }

    public static KdbxHeader Parse(ReadOnlySpan<byte> file, VaultOptions options)
    {
        if (file.Length < FixedPrefixBytes || BinaryPrimitives.ReadUInt32LittleEndian(file) != KdbxFormat.Signature1)
        {
            throw KdbxException.Corrupt();
        }
        var signature2 = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        if (signature2 is KdbxFormat.KeePass1Signature2 or KdbxFormat.PreReleaseSignature2)
        {
            throw KdbxException.Unsupported("This is a KeePass 1.x database. Import it into KeePass 2 and save it as a KDBX 4 database.");
        }
        if (signature2 != KdbxFormat.Signature2)
        {
            throw KdbxException.Corrupt();
        }
        var major = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]) >> 16;
        if (major < KdbxFormat.SupportedMajorVersion)
        {
            throw KdbxException.Unsupported("KDBX 3.1 databases are not supported. In KeePass 2.35 or newer (or KeePassXC) switch the key derivation function to Argon2 under Database Settings, Security, and save; the file is then written as KDBX 4.");
        }
        if (major > KdbxFormat.SupportedMajorVersion)
        {
            throw KdbxException.Unsupported("The database was saved in a newer KDBX format than 4.x, which Atlas cannot read yet.");
        }

        var fields = new Dictionary<byte, byte[]>();
        var position = FixedPrefixBytes;
        while (true)
        {
            if (file.Length - position < FieldPrefixBytes)
            {
                throw KdbxException.Corrupt();
            }
            var id = file[position];
            var size = BinaryPrimitives.ReadInt32LittleEndian(file[(position + 1)..]);
            if (size < 0)
            {
                throw KdbxException.Corrupt();
            }
            if (size > options.MaxHeaderFieldBytes)
            {
                throw KdbxException.Limit("header field size", nameof(VaultOptions.MaxHeaderFieldBytes));
            }
            var end = position + FieldPrefixBytes + size;
            if (end > options.MaxHeaderBytes)
            {
                throw KdbxException.Limit("header size", nameof(VaultOptions.MaxHeaderBytes));
            }
            if (end > file.Length)
            {
                throw KdbxException.Corrupt();
            }
            var data = file.Slice(position + FieldPrefixBytes, size);
            position = end;
            if (id == KdbxFormat.HeaderEnd)
            {
                break;
            }
            if (!fields.TryAdd(id, data.ToArray()))
            {
                throw KdbxException.Corrupt();
            }
        }

        // The SHA-256 detects accidental damage before any field is trusted; the HMAC (checked after key
        // derivation) authenticates the header against the key.
        if (file.Length - position < 2 * KdbxFormat.HashBytes
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(file[..position]), file.Slice(position, KdbxFormat.HashBytes)))
        {
            throw KdbxException.Corrupt();
        }
        return Interpret(fields, position, options);
    }

    private static KdbxHeader Interpret(Dictionary<byte, byte[]> fields, int length, VaultOptions options)
    {
        var cipher = Required(fields, KdbxFormat.HeaderCipherId);
        bool chaCha20;
        if (cipher.AsSpan().SequenceEqual(KdbxFormat.AesCipher))
        {
            chaCha20 = false;
        }
        else if (cipher.AsSpan().SequenceEqual(KdbxFormat.ChaCha20Cipher))
        {
            chaCha20 = true;
        }
        else if (cipher.AsSpan().SequenceEqual(KdbxFormat.TwofishCipher))
        {
            throw KdbxException.Unsupported("Twofish encryption is not supported. In KeePass choose AES-256 or ChaCha20 under Database Settings, Security, and save the database.");
        }
        else
        {
            throw KdbxException.Unsupported("The database uses an unknown encryption algorithm. In KeePass choose AES-256 or ChaCha20 under Database Settings, Security, and save the database.");
        }

        var compression = Required(fields, KdbxFormat.HeaderCompression);
        if (compression.Length != 4)
        {
            throw KdbxException.Corrupt();
        }
        var compressed = BinaryPrimitives.ReadUInt32LittleEndian(compression) switch
        {
            KdbxFormat.CompressionNone => false,
            KdbxFormat.CompressionGZip => true,
            _ => throw KdbxException.Unsupported("The database uses an unknown compression algorithm. In KeePass choose GZip or no compression and save the database."),
        };

        var masterSeed = Required(fields, KdbxFormat.HeaderMasterSeed);
        var iv = Required(fields, KdbxFormat.HeaderEncryptionIv);
        if (masterSeed.Length != KdbxFormat.MasterSeedBytes || iv.Length != (chaCha20 ? KdbxFormat.ChaCha20IvBytes : KdbxFormat.AesIvBytes))
        {
            throw KdbxException.Corrupt();
        }
        var kdf = KdbxKdfParameters.Parse(Required(fields, KdbxFormat.HeaderKdfParameters), options);
        return new KdbxHeader(length, chaCha20, compressed, masterSeed, iv, kdf);
    }

    private static byte[] Required(Dictionary<byte, byte[]> fields, byte id) =>
        fields.TryGetValue(id, out var value) ? value : throw KdbxException.Corrupt();
}
