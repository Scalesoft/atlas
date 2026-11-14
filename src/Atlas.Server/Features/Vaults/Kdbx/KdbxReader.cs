using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// Minimal reader of KDBX 4.0/4.1 files protected by a master password: AES-256 or ChaCha20, Argon2d/Argon2id
/// or AES-KDF, GZip or no compression, ChaCha20 inner stream. The order of work is fixed: size and header
/// limits, header SHA-256, KDF cost limits, (single-flight) key derivation, header HMAC, per-block HMAC,
/// decryption, then bounded streaming decompression and XML parsing with cancellation between blocks and nodes.
/// Every content problem surfaces as <see cref="KdbxException"/>.
/// </summary>
public sealed class KdbxReader
{
    private const int BlockPrefixBytes = KdbxFormat.HashBytes + 4;

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
    };

    private readonly VaultOptions m_options;
    private readonly KdbxKeyDerivation m_keyDerivation;

    public KdbxReader(VaultOptions options, KdbxKeyDerivation keyDerivation)
    {
        m_options = options;
        m_keyDerivation = keyDerivation;
    }

    /// <param name="file">The whole file, read with the <see cref="VaultOptions.MaxFileBytes"/> bound.</param>
    /// <param name="password">The master password; KeePass composite keys with key files are not supported.</param>
    /// <param name="storedKey">
    /// True when the password is the stored one that opened this file before: a mismatch then reports
    /// <see cref="KdbxFailure.KeyChanged"/> instead of <see cref="KdbxFailure.WrongKey"/>.
    /// </param>
    public KdbxDatabase Read(ReadOnlySpan<byte> file, string password, bool storedKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        var composite = KdbxKeys.Composite(password);
        try
        {
            return ReadWithCompositeKey(file, composite, storedKey, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(composite);
        }
    }

    /// <summary>
    /// The password-only KDBX composite key, SHA-256(SHA-256(UTF-8 password)). It opens the file like the password
    /// itself, so a caller that keeps it (the vault keyring) zeroes it when it drops it.
    /// </summary>
    public static byte[] CompositeKey(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return KdbxKeys.Composite(password);
    }

    /// <summary>
    /// <see cref="Read(ReadOnlySpan{byte}, string, bool, CancellationToken)"/> with a key from <see cref="CompositeKey"/>,
    /// so a caller that unlocked a file once does not keep the password string.
    /// </summary>
    public KdbxDatabase ReadWithCompositeKey(ReadOnlySpan<byte> file, ReadOnlySpan<byte> compositeKey, bool storedKey, CancellationToken cancellationToken) =>
        ReadWithCompositeKey(file, compositeKey, storedKey, null, cancellationToken);

    /// <summary>
    /// Like <see cref="ReadWithCompositeKey(ReadOnlySpan{byte}, ReadOnlySpan{byte}, bool, CancellationToken)"/>, reusing the
    /// transformed key from <paramref name="derivedKeys"/> while the header's key derivation parameters are unchanged. Every
    /// other check still runs on these bytes (header hash and HMAC, block HMACs); a derived key is remembered only after the
    /// header HMAC verified it.
    /// </summary>
    public KdbxDatabase ReadWithCompositeKey(ReadOnlySpan<byte> file, ReadOnlySpan<byte> compositeKey, bool storedKey, IKdbxDerivedKeyCache? derivedKeys,
        CancellationToken cancellationToken)
    {
        if (compositeKey.Length != KdbxFormat.HashBytes)
        {
            throw new ArgumentException("A composite key is a 32-byte SHA-256 value.", nameof(compositeKey));
        }
        if (file.Length > m_options.MaxFileBytes)
        {
            throw KdbxException.Limit("file size", nameof(VaultOptions.MaxFileBytes));
        }
        try
        {
            return ReadVerified(file, compositeKey, storedKey, derivedKeys, cancellationToken);
        }
        catch (Exception error) when (error is XmlException or FormatException or InvalidDataException or CryptographicException
            or EndOfStreamException or DecoderFallbackException or OverflowException)
        {
            // Deliberately no inner exception: parser messages can quote the decrypted document.
            throw KdbxException.Corrupt();
        }
    }

    private KdbxDatabase ReadVerified(ReadOnlySpan<byte> file, ReadOnlySpan<byte> compositeKey, bool storedKey, IKdbxDerivedKeyCache? derivedKeys,
        CancellationToken cancellationToken)
    {
        var header = KdbxHeader.Parse(file, m_options);
        cancellationToken.ThrowIfCancellationRequested();
        var kdfIdentity = derivedKeys is null ? null : header.Kdf.Identity();
        var transformed = kdfIdentity is null ? null : derivedKeys!.Find(compositeKey, kdfIdentity);
        var derived = transformed is null;
        transformed ??= m_keyDerivation.Transform(compositeKey, header.Kdf, cancellationToken);
        var hmacBase = KdbxKeys.HmacBaseKey(header.MasterSeed, transformed);
        var encryptionKey = KdbxKeys.EncryptionKey(header.MasterSeed, transformed);
        try
        {
            var expected = KdbxKeys.HeaderHmac(hmacBase, file[..header.Length]);
            if (!CryptographicOperations.FixedTimeEquals(expected, file.Slice(header.Length + KdbxFormat.HashBytes, KdbxFormat.HashBytes)))
            {
                // The header SHA-256 already matched, so a failing keyed HMAC means a different key.
                throw storedKey
                    ? new KdbxException(KdbxFailure.KeyChanged, "The stored master password no longer opens this database; it was probably changed in KeePass. Enter the current master password.")
                    : new KdbxException(KdbxFailure.WrongKey, "The password does not open this database. Only a master password is supported; databases that also require a key file or a Windows user account cannot be opened.");
            }
            if (derived && kdfIdentity is not null)
            {
                derivedKeys!.Remember(compositeKey, kdfIdentity, transformed);
            }
            var ciphertext = ReadBlocks(file[(header.Length + 2 * KdbxFormat.HashBytes)..], hmacBase, cancellationToken);
            var plain = KdbxOuterCipher.Decrypt(header.ChaCha20, encryptionKey, header.EncryptionIv, ciphertext);
            try
            {
                return ReadPayload(plain, header.Compressed, cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transformed);
            CryptographicOperations.ZeroMemory(hmacBase);
            CryptographicOperations.ZeroMemory(encryptionKey);
        }
    }

    /// <summary>Verifies the HMAC-SHA256 block stream and returns the concatenated ciphertext.</summary>
    private static ReadOnlySpan<byte> ReadBlocks(ReadOnlySpan<byte> payload, byte[] hmacBase, CancellationToken cancellationToken)
    {
        var output = new byte[payload.Length];
        var written = 0;
        var position = 0;
        for (ulong index = 0; ; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.Length - position < BlockPrefixBytes)
            {
                throw KdbxException.Corrupt();
            }
            var size = BinaryPrimitives.ReadInt32LittleEndian(payload[(position + KdbxFormat.HashBytes)..]);
            if (size < 0 || size > payload.Length - position - BlockPrefixBytes)
            {
                throw KdbxException.Corrupt();
            }
            var data = payload.Slice(position + BlockPrefixBytes, size);
            if (!CryptographicOperations.FixedTimeEquals(KdbxKeys.BlockHmac(hmacBase, index, data), payload.Slice(position, KdbxFormat.HashBytes)))
            {
                throw KdbxException.Corrupt();
            }
            position += BlockPrefixBytes + size;
            if (size == 0)
            {
                // The empty block terminates the stream; anything after it is not authenticated content.
                if (position != payload.Length)
                {
                    throw KdbxException.Corrupt();
                }
                return output.AsSpan(0, written);
            }
            data.CopyTo(output.AsSpan(written));
            written += size;
        }
    }

    private KdbxDatabase ReadPayload(byte[] plain, bool compressed, CancellationToken cancellationToken)
    {
        using var source = new MemoryStream(plain, false);
        using var decoded = compressed ? new GZipStream(source, CompressionMode.Decompress, true) : (Stream)source;
        // Six raw bytes per field byte covers entity-escaped text such as "&amp;".
        using var bounded = new KdbxBoundedStream(decoded, m_options.MaxDecompressedBytes, 6L * m_options.MaxXmlFieldBytes + 64 * 1024);
        var inner = ReadInnerHeader(bounded, cancellationToken);
        bounded.GuardMarkup = true;
        using var xml = XmlReader.Create(bounded, XmlSettings);
        return new KdbxXmlParser(xml, inner, m_options, cancellationToken).Parse();
    }

    private KdbxInnerStream ReadInnerHeader(KdbxBoundedStream payload, CancellationToken cancellationToken)
    {
        uint? streamId = null;
        byte[]? key = null;
        Span<byte> prefix = stackalloc byte[5];
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                payload.ReadExact(prefix);
                var id = prefix[0];
                var size = BinaryPrimitives.ReadInt32LittleEndian(prefix[1..]);
                if (size < 0)
                {
                    throw KdbxException.Corrupt();
                }
                if (id == KdbxFormat.InnerBinary)
                {
                    // Attachments are never loaded; their bytes still count against the decompressed limit.
                    payload.Skip(size, cancellationToken);
                    continue;
                }
                if (size > m_options.MaxHeaderFieldBytes)
                {
                    throw KdbxException.Limit("header field size", nameof(VaultOptions.MaxHeaderFieldBytes));
                }
                var data = new byte[size];
                payload.ReadExact(data);
                if (id == KdbxFormat.InnerEnd)
                {
                    break;
                }
                if (id == KdbxFormat.InnerStreamId)
                {
                    if (size != 4 || streamId is not null)
                    {
                        throw KdbxException.Corrupt();
                    }
                    streamId = BinaryPrimitives.ReadUInt32LittleEndian(data);
                }
                else if (id == KdbxFormat.InnerStreamKey)
                {
                    if (size == 0 || key is not null)
                    {
                        CryptographicOperations.ZeroMemory(data);
                        throw KdbxException.Corrupt();
                    }
                    key = data;
                }
            }
            if (streamId is null || key is null)
            {
                throw KdbxException.Corrupt();
            }
            if (streamId != KdbxFormat.InnerStreamChaCha20)
            {
                throw KdbxException.Unsupported("The database protects fields with a legacy inner stream (Salsa20). Save it with KeePass 2.35 or newer as KDBX 4.");
            }
            return new KdbxInnerStream(key);
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }
}
