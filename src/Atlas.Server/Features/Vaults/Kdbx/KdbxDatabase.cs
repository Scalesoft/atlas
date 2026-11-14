namespace Atlas.Server.Features.Vaults.Kdbx;

/// <summary>
/// The subset of a KDBX file Atlas uses. History, attachments, custom icons, custom data and deleted objects
/// are skipped. Dispose promptly to zero the decrypted values.
/// </summary>
public sealed class KdbxDatabase : IDisposable
{
    internal KdbxDatabase(Guid rootGroupUuid, string databaseName, Guid? recycleBinUuid, IReadOnlyList<KdbxEntry> entries)
    {
        RootGroupUuid = rootGroupUuid;
        DatabaseName = databaseName;
        RecycleBinUuid = recycleBinUuid;
        Entries = entries;
    }

    public Guid RootGroupUuid { get; }

    public string DatabaseName { get; }

    /// <summary>Null when the recycle bin is disabled or not created yet.</summary>
    public Guid? RecycleBinUuid { get; }

    public IReadOnlyList<KdbxEntry> Entries { get; }

    public void Dispose()
    {
        foreach (var entry in Entries)
        {
            entry.Dispose();
        }
    }

    public override string ToString() => $"KeePass database with {Entries.Count} entries";
}
