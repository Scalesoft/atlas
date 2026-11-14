using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// Creates a new, empty KDBX 4.0 database (root group and recycle bin): Argon2id, ChaCha20 payload and inner
/// stream, GZip. Existing databases are never written. The result is read back with <see cref="KdbxReader"/>
/// before it is returned, so a file that Atlas itself could not open is never published.
/// </summary>
public sealed class KdbxWriter
{
    private const int BlockBytes = 1024 * 1024;
    private const string ZeroUuid = "AAAAAAAAAAAAAAAAAAAAAA==";

    private readonly VaultOptions m_options;
    private readonly KdbxKeyDerivation m_keyDerivation;
    private readonly KdbxReader m_reader;

    public KdbxWriter(VaultOptions options, KdbxKeyDerivation keyDerivation, KdbxReader reader)
    {
        m_options = options;
        m_keyDerivation = keyDerivation;
        m_reader = reader;
    }

    public byte[] CreateEmpty(string password, string databaseName, KdbxNewDatabaseSettings settings, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(databaseName);
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        var rootUuid = Guid.NewGuid();
        var recycleBinUuid = Guid.NewGuid();
        var masterSeed = RandomNumberGenerator.GetBytes(KdbxFormat.MasterSeedBytes);
        var iv = RandomNumberGenerator.GetBytes(KdbxFormat.ChaCha20IvBytes);
        var kdf = KdbxKdfParameters.Argon2id(RandomNumberGenerator.GetBytes(32), settings.Argon2MemoryBytes, settings.Argon2Iterations, settings.Argon2Parallelism);
        var header = Header(masterSeed, iv, kdf);

        var composite = KdbxKeys.Composite(password);
        byte[] transformed;
        try
        {
            transformed = m_keyDerivation.Transform(composite, kdf, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(composite);
        }
        var hmacBase = KdbxKeys.HmacBaseKey(masterSeed, transformed);
        var encryptionKey = KdbxKeys.EncryptionKey(masterSeed, transformed);
        CryptographicOperations.ZeroMemory(transformed);
        byte[] file;
        try
        {
            var ciphertext = KdbxOuterCipher.ChaCha20(encryptionKey, iv, Payload(databaseName, rootUuid, recycleBinUuid));
            using var output = new MemoryStream();
            output.Write(header);
            output.Write(SHA256.HashData(header));
            output.Write(KdbxKeys.HeaderHmac(hmacBase, header));
            WriteBlocks(output, ciphertext, hmacBase);
            file = output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hmacBase);
            CryptographicOperations.ZeroMemory(encryptionKey);
        }

        using var verified = m_reader.Read(file, password, false, cancellationToken);
        if (verified.RootGroupUuid != rootUuid || verified.RecycleBinUuid != recycleBinUuid || verified.Entries.Count != 0)
        {
            throw new InvalidOperationException("The new KeePass database did not read back as written.");
        }
        return file;
    }

    private void Validate(KdbxNewDatabaseSettings settings)
    {
        // The reader must accept what the writer produces, so the same limits apply.
        if (settings.Argon2MemoryBytes % 1024 != 0 || settings.Argon2MemoryBytes > (ulong)m_options.MaxArgon2MemoryBytes
            || settings.Argon2Parallelism == 0 || settings.Argon2Parallelism > (uint)m_options.MaxArgon2Parallelism
            || settings.Argon2MemoryBytes / 1024 < 8UL * settings.Argon2Parallelism
            || settings.Argon2Iterations == 0 || settings.Argon2Iterations > (ulong)m_options.MaxArgon2Iterations)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Argon2 settings are outside the configured vault limits.");
        }
    }

    private static byte[] Header(byte[] masterSeed, byte[] iv, KdbxKdfParameters kdf)
    {
        using var output = new MemoryStream();
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(number, KdbxFormat.Signature1);
        output.Write(number);
        BinaryPrimitives.WriteUInt32LittleEndian(number, KdbxFormat.Signature2);
        output.Write(number);
        BinaryPrimitives.WriteUInt32LittleEndian(number, KdbxFormat.Version40);
        output.Write(number);
        WriteField(output, KdbxFormat.HeaderCipherId, KdbxFormat.ChaCha20Cipher);
        BinaryPrimitives.WriteUInt32LittleEndian(number, KdbxFormat.CompressionGZip);
        WriteField(output, KdbxFormat.HeaderCompression, number);
        WriteField(output, KdbxFormat.HeaderMasterSeed, masterSeed);
        WriteField(output, KdbxFormat.HeaderEncryptionIv, iv);
        WriteField(output, KdbxFormat.HeaderKdfParameters, kdf.ToArgon2Dictionary());
        WriteField(output, KdbxFormat.HeaderEnd, "\r\n\r\n"u8);
        return output.ToArray();
    }

    private static void WriteField(Stream output, byte id, ReadOnlySpan<byte> data)
    {
        Span<byte> prefix = stackalloc byte[5];
        prefix[0] = id;
        BinaryPrimitives.WriteInt32LittleEndian(prefix[1..], data.Length);
        output.Write(prefix);
        output.Write(data);
    }

    /// <summary>GZip of the inner header (ChaCha20 stream with a fresh key) followed by the XML document.</summary>
    private static byte[] Payload(string databaseName, Guid rootUuid, Guid recycleBinUuid)
    {
        var innerKey = RandomNumberGenerator.GetBytes(64);
        try
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true))
            {
                Span<byte> number = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(number, KdbxFormat.InnerStreamChaCha20);
                WriteField(gzip, KdbxFormat.InnerStreamId, number);
                WriteField(gzip, KdbxFormat.InnerStreamKey, innerKey);
                WriteField(gzip, KdbxFormat.InnerEnd, []);
                WriteXml(gzip, databaseName, rootUuid, recycleBinUuid);
            }
            return compressed.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(innerKey);
        }
    }

    private static void WriteXml(Stream output, string databaseName, Guid rootUuid, Guid recycleBinUuid)
    {
        var now = Time(DateTime.UtcNow);
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "\t", CloseOutput = false };
        using var xml = XmlWriter.Create(output, settings);
        xml.WriteStartDocument(true);
        xml.WriteStartElement("KeePassFile");
        xml.WriteStartElement("Meta");
        xml.WriteElementString("Generator", "Atlas");
        xml.WriteElementString("DatabaseName", databaseName);
        xml.WriteElementString("DatabaseNameChanged", now);
        xml.WriteElementString("DatabaseDescription", "");
        xml.WriteElementString("DatabaseDescriptionChanged", now);
        xml.WriteElementString("DefaultUserName", "");
        xml.WriteElementString("DefaultUserNameChanged", now);
        xml.WriteElementString("MaintenanceHistoryDays", "365");
        xml.WriteElementString("Color", "");
        xml.WriteElementString("MasterKeyChanged", now);
        xml.WriteElementString("MasterKeyChangeRec", "-1");
        xml.WriteElementString("MasterKeyChangeForce", "-1");
        xml.WriteStartElement("MemoryProtection");
        xml.WriteElementString("ProtectTitle", "False");
        xml.WriteElementString("ProtectUserName", "False");
        xml.WriteElementString("ProtectPassword", "True");
        xml.WriteElementString("ProtectURL", "False");
        xml.WriteElementString("ProtectNotes", "False");
        xml.WriteEndElement();
        xml.WriteElementString("RecycleBinEnabled", "True");
        xml.WriteElementString("RecycleBinUUID", Uuid(recycleBinUuid));
        xml.WriteElementString("RecycleBinChanged", now);
        xml.WriteElementString("EntryTemplatesGroup", ZeroUuid);
        xml.WriteElementString("EntryTemplatesGroupChanged", now);
        xml.WriteElementString("HistoryMaxItems", "10");
        xml.WriteElementString("HistoryMaxSize", "6291456");
        xml.WriteElementString("LastSelectedGroup", ZeroUuid);
        xml.WriteElementString("LastTopVisibleGroup", ZeroUuid);
        xml.WriteEndElement();
        xml.WriteStartElement("Root");
        WriteGroupStart(xml, rootUuid, databaseName, 49, true, "null", now);
        WriteGroupStart(xml, recycleBinUuid, "Recycle Bin", 43, false, "false", now);
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteStartElement("DeletedObjects");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void WriteGroupStart(XmlWriter xml, Guid uuid, string name, int icon, bool expanded, string enabled, string now)
    {
        xml.WriteStartElement("Group");
        xml.WriteElementString("UUID", Uuid(uuid));
        xml.WriteElementString("Name", name);
        xml.WriteElementString("Notes", "");
        xml.WriteElementString("IconID", icon.ToString(System.Globalization.CultureInfo.InvariantCulture));
        xml.WriteStartElement("Times");
        xml.WriteElementString("CreationTime", now);
        xml.WriteElementString("LastModificationTime", now);
        xml.WriteElementString("LastAccessTime", now);
        xml.WriteElementString("ExpiryTime", now);
        xml.WriteElementString("Expires", "False");
        xml.WriteElementString("UsageCount", "0");
        xml.WriteElementString("LocationChanged", now);
        xml.WriteEndElement();
        xml.WriteElementString("IsExpanded", expanded ? "True" : "False");
        xml.WriteElementString("DefaultAutoTypeSequence", "");
        xml.WriteElementString("EnableAutoType", enabled);
        xml.WriteElementString("EnableSearching", enabled);
        xml.WriteElementString("LastTopVisibleEntry", ZeroUuid);
    }

    private static void WriteBlocks(Stream output, ReadOnlySpan<byte> ciphertext, byte[] hmacBase)
    {
        Span<byte> size = stackalloc byte[4];
        ulong index = 0;
        var position = 0;
        while (true)
        {
            var block = ciphertext.Slice(position, Math.Min(BlockBytes, ciphertext.Length - position));
            output.Write(KdbxKeys.BlockHmac(hmacBase, index, block));
            BinaryPrimitives.WriteInt32LittleEndian(size, block.Length);
            output.Write(size);
            output.Write(block);
            if (block.Length == 0)
            {
                return;
            }
            position += block.Length;
            index++;
        }
    }

    private static string Uuid(Guid uuid) => Convert.ToBase64String(uuid.ToByteArray());

    private static string Time(DateTime utc)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, utc.Ticks / TimeSpan.TicksPerSecond);
        return Convert.ToBase64String(bytes);
    }
}
