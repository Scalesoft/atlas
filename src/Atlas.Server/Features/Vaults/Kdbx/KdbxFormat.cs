namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>Constants of the published KDBX 4.x file format.</summary>
internal static class KdbxFormat
{
    public const uint Signature1 = 0x9AA2D903;
    public const uint Signature2 = 0xB54BFB67;
    public const uint KeePass1Signature2 = 0xB54BFB65;
    public const uint PreReleaseSignature2 = 0xB54BFB66;
    public const uint Version40 = 0x00040000;
    public const int SupportedMajorVersion = 4;

    // Outer header field identifiers.
    public const byte HeaderEnd = 0;
    public const byte HeaderCipherId = 2;
    public const byte HeaderCompression = 3;
    public const byte HeaderMasterSeed = 4;
    public const byte HeaderEncryptionIv = 7;
    public const byte HeaderKdfParameters = 11;

    // Inner header field identifiers.
    public const byte InnerEnd = 0;
    public const byte InnerStreamId = 1;
    public const byte InnerStreamKey = 2;
    public const byte InnerBinary = 3;

    public const uint InnerStreamChaCha20 = 3;
    public const uint CompressionNone = 0;
    public const uint CompressionGZip = 1;

    public const int MasterSeedBytes = 32;
    public const int AesIvBytes = 16;
    public const int ChaCha20IvBytes = 12;
    public const int HashBytes = 32;

    public const ushort VariantDictionaryVersion = 0x0100;

    public static ReadOnlySpan<byte> AesCipher => [0x31, 0xC1, 0xF2, 0xE6, 0xBF, 0x71, 0x43, 0x50, 0xBE, 0x58, 0x05, 0x21, 0x6A, 0xFC, 0x5A, 0xFF];

    public static ReadOnlySpan<byte> ChaCha20Cipher => [0xD6, 0x03, 0x8A, 0x2B, 0x8B, 0x6F, 0x4C, 0xB5, 0xA5, 0x24, 0x33, 0x9A, 0x31, 0xDB, 0xB5, 0x9A];

    public static ReadOnlySpan<byte> TwofishCipher => [0xAD, 0x68, 0xF2, 0x9F, 0x57, 0x6F, 0x4B, 0xB9, 0xA3, 0x6A, 0xD4, 0x7A, 0xF9, 0x65, 0x34, 0x6C];

    public static ReadOnlySpan<byte> AesKdf => [0xC9, 0xD9, 0xF3, 0x9A, 0x62, 0x8A, 0x44, 0x60, 0xBF, 0x74, 0x0D, 0x08, 0xC1, 0x8A, 0x4F, 0xEA];

    public static ReadOnlySpan<byte> Argon2dKdf => [0xEF, 0x63, 0x6D, 0xDF, 0x8C, 0x29, 0x44, 0x4B, 0x91, 0xF7, 0xA9, 0xA4, 0x03, 0xE3, 0x0A, 0x0C];

    public static ReadOnlySpan<byte> Argon2idKdf => [0x9E, 0x29, 0x8B, 0x19, 0x56, 0xDB, 0x47, 0x73, 0xB2, 0x3D, 0xFC, 0x3E, 0xC6, 0xF0, 0xA1, 0xE6];
}
