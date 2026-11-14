using System.Security.Cryptography;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Kdbx;
using Atlas.Server.Platform.Storage;

namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>
/// Reads and creates KDBX files on a vault location. Storage calls are asynchronous; parsing, key derivation and
/// writing are synchronous and run on the thread pool. Decrypted values never leave this class: a read returns
/// entry metadata only and disposes the database immediately.
/// </summary>
public sealed class VaultFiles
{
    private readonly KdbxReader m_reader;
    private readonly KdbxWriter m_writer;
    private readonly VaultOptions m_options;

    public VaultFiles(KdbxReader reader, KdbxWriter writer, VaultOptions options)
    {
        m_reader = reader;
        m_writer = writer;
        m_options = options;
    }

    public async Task<FileStorageMetadata> GetMetadataAsync(FileStorageOptions storage, string fileName, CancellationToken ct)
    {
        using var files = FileStorages.Open(storage);
        return await files.GetMetadataAsync(fileName, ct);
    }

    /// <summary>
    /// Reads the file and decrypts it with the key, unless its SHA-256 equals <paramref name="knownSha256"/>: then
    /// no key derivation runs and the result has no entries. KDBX problems surface as <see cref="KdbxException"/>.
    /// </summary>
    public async Task<VaultFileRead> ReadAsync(FileStorageOptions storage, string fileName, byte[] compositeKey, bool storedKey,
        byte[]? knownSha256, CancellationToken ct, IKdbxDerivedKeyCache? derivedKeys = null)
    {
        using var files = FileStorages.Open(storage);
        var metadata = await files.GetMetadataAsync(fileName, ct);
        var content = await files.ReadBoundedAsync(fileName, m_options.MaxFileBytes, ct);
        var sha256 = SHA256.HashData(content);
        if (knownSha256 is not null && CryptographicOperations.FixedTimeEquals(sha256, knownSha256))
        {
            return new VaultFileRead(sha256, metadata, null);
        }
        var entries = await Task.Run(() => ReadEntries(content, compositeKey, storedKey, derivedKeys, ct), ct);
        return new VaultFileRead(sha256, metadata, entries);
    }

    /// <summary>
    /// Reads the file now and returns copies of the requested fields of one current entry (outside the recycle bin, the first
    /// occurrence of its UUID as the registry keeps it) as UTF-8 bytes the caller zeroes, or null when the entry is not in the file
    /// any more. The database and every other value are disposed before returning; no value is cached (only the transformed key
    /// in <paramref name="derivedKeys"/>, which still needs these bytes to verify). KDBX problems surface as KdbxException.
    /// </summary>
    public async Task<IReadOnlyDictionary<VaultSecretFields, byte[]>?> ReadFieldsAsync(FileStorageOptions storage, string fileName,
        byte[] compositeKey, Guid entryUuid, IReadOnlyCollection<VaultSecretFields> fields, CancellationToken ct, IKdbxDerivedKeyCache? derivedKeys = null)
    {
        using var files = FileStorages.Open(storage);
        var content = await files.ReadBoundedAsync(fileName, m_options.MaxFileBytes, ct);
        return await Task.Run(() => ReadFields(content, compositeKey, entryUuid, fields, derivedKeys, ct), ct);
    }

    /// <summary>Whether a file of that name already exists in the location.</summary>
    public async Task<bool> ExistsAsync(FileStorageOptions storage, string fileName, CancellationToken ct)
    {
        using var files = FileStorages.Open(storage);
        return await files.ExistsAsync(fileName, ct);
    }

    /// <summary>Writes a new empty database, publishes it without replacing an existing file and returns the SHA-256 of the bytes written.</summary>
    public async Task<byte[]> CreateAsync(FileStorageOptions storage, string fileName, string password, string databaseName, CancellationToken ct)
    {
        var content = await Task.Run(() => m_writer.CreateEmpty(password, databaseName, new KdbxNewDatabaseSettings(), ct), ct);
        using var files = FileStorages.Open(storage);
        using var stream = new MemoryStream(content, false);
        await files.PublishNewAsync(fileName, stream, ct);
        return SHA256.HashData(content);
    }

    /// <summary>Removes a file this process just created and could not register.</summary>
    public async Task DeleteAsync(FileStorageOptions storage, string fileName, CancellationToken ct)
    {
        using var files = FileStorages.Open(storage);
        await files.DeleteAsync(fileName, ct);
    }

    private List<VaultSnapshotEntry> ReadEntries(byte[] content, byte[] compositeKey, bool storedKey, IKdbxDerivedKeyCache? derivedKeys, CancellationToken ct)
    {
        using var database = m_reader.ReadWithCompositeKey(content, compositeKey, storedKey, derivedKeys, ct);
        var entries = new List<VaultSnapshotEntry>(database.Entries.Count);
        var seen = new HashSet<Guid>();
        foreach (var entry in database.Entries)
        {
            // A crafted file could repeat a UUID; the registry keeps the first occurrence.
            if (entry.InRecycleBin || !seen.Add(entry.Uuid))
            {
                continue;
            }
            var fields = VaultSecretFields.None;
            fields |= entry.UserName.Length > 0 ? VaultSecretFields.UserName : VaultSecretFields.None;
            fields |= entry.Password.Length > 0 ? VaultSecretFields.Password : VaultSecretFields.None;
            fields |= entry.Url.Length > 0 ? VaultSecretFields.Url : VaultSecretFields.None;
            fields |= entry.Notes.Length > 0 ? VaultSecretFields.Notes : VaultSecretFields.None;
            // Read from the password file: control and bidirectional characters are removed (Rules.CleanLine), never refusing the sync.
            entries.Add(new VaultSnapshotEntry(entry.Uuid, Truncate(Rules.CleanLine(entry.Title), VaultSecret.TitleMaxLength),
                Truncate(Rules.CleanLine(string.Join(" / ", entry.GroupPath)), VaultSecret.GroupPathMaxLength), fields));
        }
        return entries;
    }

    private Dictionary<VaultSecretFields, byte[]>? ReadFields(byte[] content, byte[] compositeKey, Guid entryUuid,
        IReadOnlyCollection<VaultSecretFields> fields, IKdbxDerivedKeyCache? derivedKeys, CancellationToken ct)
    {
        using var database = m_reader.ReadWithCompositeKey(content, compositeKey, true, derivedKeys, ct);
        var entry = database.Entries.FirstOrDefault(e => e.Uuid == entryUuid && !e.InRecycleBin);
        if (entry is null)
        {
            return null;
        }
        return fields.Distinct().ToDictionary(field => field, field => (field switch
        {
            VaultSecretFields.UserName => entry.UserName,
            VaultSecretFields.Password => entry.Password,
            VaultSecretFields.Url => entry.Url,
            VaultSecretFields.Notes => entry.Notes,
            _ => throw new ArgumentOutOfRangeException(nameof(fields))
        }).Utf8.ToArray());
    }

    private static string Truncate(string value, int maximum)
    {
        if (value.Length <= maximum)
        {
            return value;
        }
        // Never split a surrogate pair.
        return value[..(char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum)];
    }
}
