using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Backup;

/// <summary>
/// One module's part of the installation backup (Atlas 3 plan, chapter 15; format v7 in docs/backup-format-v7.md). Every module
/// of the distribution has one whether it is enabled or not: it uses only the session and platform storage, never the services
/// of the running module, so a disabled module's data is still backed up and restored.
/// </summary>
public interface IModuleBackup
{
    /// <summary>The folder name under <c>.atlas/modules</c> and <c>.atlas/secure</c>.</summary>
    string ModuleId { get; }

    /// <summary>Raised when the module's files change meaning; a restore refuses another version than its own.</summary>
    int FormatVersion { get; }

    /// <summary>Modules whose rows this module's rows reference: they restore first.</summary>
    IReadOnlyList<string> DependsOn { get; }

    /// <summary>The tables the module backs up; a table no module lists must be declared derived or operational (coverage test).</summary>
    IReadOnlyList<BackupTable> Tables { get; }

    /// <summary>A cheap value that changes whenever a backed-up row of the module changes.</summary>
    Task<long> ChangeStampAsync(ISession session, CancellationToken ct);

    /// <summary>Every file of the module's part, in a stable order. The same rows give the same bytes.</summary>
    IAsyncEnumerable<BackupFile> ExportAsync(ISession session, BackupExport export, CancellationToken ct);

    /// <summary>Reads and checks the module's part of a backup without writing anything; problems block the restore.</summary>
    Task ValidateAsync(ISession session, RestoreContext context, CancellationToken ct);

    /// <summary>Inserts the module's rows into the emptied tables (triggers off) and writes its files.</summary>
    Task RestoreAsync(ISession session, RestoreContext context, CancellationToken ct);

    /// <summary>Recomputes what the module derives from its rows (search documents, link tables) after all modules restored.</summary>
    Task RebuildAsync(ISession session, CancellationToken ct);
}

/// <summary>A backed-up table. A secure table is written encrypted under <c>.atlas/secure</c> and restored only with the backup key;
/// <paramref name="Protected"/> maps its Data Protection columns to their purposes: they travel decrypted inside the encrypted
/// file and are protected again by the restoring host. <paramref name="Cleared"/> maps a reference column a restore without the
/// secure part clears to the values it then sets, where a check couples them (an API revision without its token).
/// <paramref name="Retired"/> names columns a migration dropped: a backup from before it still restores, without their values.</summary>
public sealed record BackupTable(string Name, bool Secure = false, IReadOnlyDictionary<string, string>? Protected = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Cleared = null, IReadOnlyList<string>? Retired = null);

/// <summary>One file of the backup tree. <paramref name="Open"/> is null for a file that has not changed since
/// <see cref="BackupExport.ChangedSince"/>: the transport keeps what it already holds.</summary>
public sealed record BackupFile(string Path, Func<Stream>? Open);

/// <param name="Key">Encrypts the secure part; without it the secure part is left out.</param>
/// <param name="ChangedSince">A 32-bit transaction id: rows whose xmin is lower are unchanged since the previous export. Null exports all.</param>
public sealed record BackupExport(BackupKey? Key, long? ChangedSince);

public enum RestoreMode
{
    /// <summary>Everything, the secure part included (the backup key matched).</summary>
    Full,

    /// <summary>Without the backup key: content and settings without secrets; accounts become inactive placeholders, no grants.</summary>
    ContentOnly
}

/// <summary>What a validation or a restore needs and what it reports.</summary>
public sealed class RestoreContext(IBackupReader reader, RestoreMode mode, IReadOnlySet<string> restoredTables)
{
    public IBackupReader Reader { get; } = reader;
    public RestoreMode Mode { get; } = mode;

    /// <summary>The tables whose rows exist after this restore, across modules (in content-only mode also UserAccount, whose rows
    /// are placeholders): a reference to any other table is cleared, or its row left out.</summary>
    public IReadOnlySet<string> RestoredTables { get; } = restoredTables;

    /// <summary>Block the restore.</summary>
    public List<string> Problems { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>Rows per <c>module/Table</c>.</summary>
    public SortedDictionary<string, int> Rows { get; } = new(StringComparer.Ordinal);

    /// <summary>Files written outside the database; removed again when the restore rolls back.</summary>
    public List<string> CreatedFiles { get; } = [];
}

/// <summary>A backup tree being read: a Git clone or an extracted ZIP whose files the manifest lists with their SHA-256.</summary>
public interface IBackupReader
{
    BackupManifest Manifest { get; }

    /// <summary>The backup key, only when it is the one the backup was written with.</summary>
    BackupKey? Key { get; }

    /// <summary>The manifest's paths that start with <paramref name="prefix"/>, ordinal order.</summary>
    IEnumerable<string> Files(string prefix);

    Stream Open(string path);

    /// <summary>Decrypts a file of the secure part; needs <see cref="Key"/>.</summary>
    byte[] ReadSecure(string path);
}

/// <summary>.atlas/manifest.json: no time of the run, so an unchanged installation writes the same bytes.</summary>
/// <param name="Schema">The newest applied migration of the exporting installation.</param>
/// <param name="KeyId">The id of the key the secure part is encrypted with; null when the backup has no secure part.</param>
public sealed record BackupManifest(string Format, int Version, long Schema, string? KeyId,
    SortedDictionary<string, int> Modules, SortedDictionary<string, string> Files)
{
    public const string Path = ".atlas/manifest.json";
    public const string FormatName = "atlas-backup";
    public const int CurrentVersion = 7;
}
