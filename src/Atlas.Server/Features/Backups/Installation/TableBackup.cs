using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Platform.Backup;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Backups.Installation;

/// <summary>Files a module writes next to its rows: blobs, account names, vault files.</summary>
internal interface IBackupPart
{
    /// <summary>The folder inside the module's folders the part writes to; null when it writes elsewhere (blobs).</summary>
    string? Folder { get; }

    IAsyncEnumerable<BackupFile> ExportAsync(ISession session, BackupExport export, CancellationToken ct);

    Task ValidateAsync(ISession session, RestoreContext context, CancellationToken ct);

    Task RestoreAsync(ISession session, RestoreContext context, CancellationToken ct);
}

/// <summary>
/// A module backup over its own tables, without a line of per-entity code: every row is one canonical JSON file named by its
/// primary key (<c>.atlas/modules/&lt;module&gt;/&lt;Table&gt;/&lt;key&gt;.json</c>, encrypted under <c>.atlas/secure</c> for a secure
/// table), exactly as PostgreSQL stores it, and the restore inserts it back unchanged. The real schema decides the columns, so a
/// new column is backed up and restored without a change here; a column the running schema does not know refuses the restore.
/// </summary>
public sealed class TableBackup : IModuleBackup
{
    private static readonly Regex SafeKey = new("^[0-9a-z-]{1,64}$", RegexOptions.CultureInvariant);
    private const int FetchSize = 500;
    private const int BatchCharacters = 4 * 1024 * 1024;
    private const int MaximumReportedProblems = 50;
    private readonly IDataProtectionProvider m_protection;
    private readonly IReadOnlyList<IBackupPart> m_parts;
    private readonly string? m_rebuild;
    private readonly Func<ISession, CancellationToken, Task>? m_rebuildInCode;

    /// <param name="rebuild">SQL that recomputes the module's derived tables after the restore, when the triggers are on again.</param>
    /// <param name="rebuildInCode">The same in code, after <paramref name="rebuild"/>, for derived tables no SQL keeps (the requirement index).</param>
    internal TableBackup(string moduleId, int formatVersion, IReadOnlyList<string> dependsOn, IReadOnlyList<BackupTable> tables,
        IDataProtectionProvider protection, IReadOnlyList<IBackupPart>? parts = null, string? rebuild = null,
        Func<ISession, CancellationToken, Task>? rebuildInCode = null)
    {
        m_rebuildInCode = rebuildInCode;
        ModuleId = moduleId;
        FormatVersion = formatVersion;
        DependsOn = dependsOn;
        Tables = tables;
        m_protection = protection;
        m_parts = parts ?? [];
        m_rebuild = rebuild;
    }

    public string ModuleId { get; }
    public int FormatVersion { get; }
    public IReadOnlyList<string> DependsOn { get; }
    public IReadOnlyList<BackupTable> Tables { get; }

    public static string Folder(string moduleId, bool secure) => $".atlas/{(secure ? "secure" : "modules")}/{moduleId}/";

    /// <summary>Row count and the sum of the row versions (xmin) of every table: any insert, update or delete moves it.</summary>
    public async Task<long> ChangeStampAsync(ISession session, CancellationToken ct)
    {
        var sql = "SELECT " + string.Join(" || ';' || ", Tables.Select(table =>
            $"(SELECT count(*) || ':' || coalesce(sum(CAST(CAST(xmin AS text) AS bigint)), 0) FROM {TableSchema.Quote(table.Name)})"));
        var text = await session.CreateSQLQuery(sql).UniqueResultAsync<string>(ct);
        return BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public async IAsyncEnumerable<BackupFile> ExportAsync(ISession session, BackupExport export, [EnumeratorCancellation] CancellationToken ct)
    {
        var schema = await TableSchema.ReadAsync(session, ct);
        foreach (var table in Tables.Where(x => !x.Secure || export.Key is not null))
        {
            await foreach (var file in ExportRowsAsync(session, schema[table.Name], table, export, ct))
            {
                yield return file;
            }
        }
        foreach (var part in m_parts)
        {
            await foreach (var file in part.ExportAsync(session, export, ct))
            {
                yield return file;
            }
        }
    }

    private async IAsyncEnumerable<BackupFile> ExportRowsAsync(ISession session, TableInfo info, BackupTable table, BackupExport export,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (info.Key.Count == 0)
        {
            throw new InvalidOperationException($"Table {info.Name} has no primary key; its rows cannot be named in the backup.");
        }
        var key = string.Join(", ", info.Key.Select(column => $"CAST(t.{TableSchema.Quote(column)} AS text)"));
        var row = "to_jsonb(t)" + string.Concat(info.Columns.Values.Where(x => x.Generated).Select(x => " - '" + x.Name.Replace("'", "''") + "'"));
        var changed = export.ChangedSince is { } since ? $"CAST(CAST(t.xmin AS text) AS bigint) >= {since}" : "true";
        var order = string.Join(", ", info.Key.Select(column => "t." + TableSchema.Quote(column)));
        var folder = Folder(ModuleId, table.Secure) + table.Name + "/";
        // A cursor streams a table of any size within the export's snapshot.
        await session.CreateSQLQuery($"DECLARE atlas_backup_rows NO SCROLL CURSOR FOR SELECT CAST(json_build_array({key}) AS text) AS row_key, "
            + $"CASE WHEN {changed} THEN CAST({row} AS text) END AS row_json FROM {TableSchema.Quote(info.Name)} t ORDER BY {order}").ExecuteUpdateAsync(ct);
        try
        {
            while (true)
            {
                var rows = await session.CreateSQLQuery($"FETCH {FetchSize} FROM atlas_backup_rows").ListAsync<object[]>(ct);
                if (rows.Count == 0)
                {
                    break;
                }
                foreach (var item in rows)
                {
                    var path = folder + FileKey((string)item[0]) + (table.Secure ? ".json.enc" : ".json");
                    if (item[1] is not string json)
                    {
                        yield return new BackupFile(path, null);
                        continue;
                    }
                    var bytes = Serialize(table, path, json, export.Key);
                    yield return new BackupFile(path, () => new MemoryStream(bytes, false));
                }
            }
        }
        finally
        {
            await session.CreateSQLQuery("CLOSE atlas_backup_rows").ExecuteUpdateAsync(CancellationToken.None);
        }
    }

    private byte[] Serialize(BackupTable table, string path, string json, BackupKey? key)
    {
        var row = CanonicalJson.Parse(json);
        foreach (var (column, purpose) in table.Protected ?? new Dictionary<string, string>())
        {
            // An empty value is no secret (a YouTrack token cleared, an AI profile without a key): it travels as it is, like on restore.
            if (row[column]?.GetValue<string>() is { Length: > 0 } stored)
            {
                try
                {
                    row[column] = m_protection.CreateProtector(purpose).Unprotect(stored);
                }
                catch (CryptographicException)
                {
                    // Unreadable on this host already (lost key ring): no secret (null; the restore gives a NOT NULL column ""), and
                    // the administrator enters it again, as the feature itself asks.
                    row[column] = null;
                }
            }
        }
        var bytes = CanonicalJson.Write(row);
        return table.Secure ? key!.Encrypt(path, bytes) : bytes;
    }

    /// <summary>The primary key as a file name: the values joined by "_" when they are lowercase ids or numbers, else a hash, so a
    /// name is always safe and unique on case-insensitive file systems too.</summary>
    private static string FileKey(string keyJson)
    {
        var parts = JsonSerializer.Deserialize<string[]>(keyJson)!;
        return parts.All(SafeKey.IsMatch) ? string.Join('_', parts) : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keyJson)))[..32];
    }

    public async Task ValidateAsync(ISession session, RestoreContext context, CancellationToken ct)
    {
        var manifest = context.Reader.Manifest;
        if (!manifest.Modules.TryGetValue(ModuleId, out var version))
        {
            context.Warnings.Add($"Záloha neobsahuje modul {ModuleId}; jeho data budou po obnově prázdná.");
            return;
        }
        if (version != FormatVersion)
        {
            context.Problems.Add($"Modul {ModuleId} je v záloze ve formátu {version}, tato verze Atlasu čte formát {FormatVersion}. "
                + "Obnovte zálohu verzí Atlasu, která ji vytvořila, a aktualizujte běžnou cestou.");
            return;
        }
        var schema = await TableSchema.ReadAsync(session, ct);
        ValidateFolders(context);
        foreach (var table in Tables.Where(x => !x.Secure || context.Mode == RestoreMode.Full))
        {
            var count = 0;
            foreach (var (path, row) in Rows(context.Reader, ModuleId, table))
            {
                count++;
                var unknown = row.Select(x => x.Key).Where(x => !schema[table.Name].Columns.TryGetValue(x, out var column) || column.Generated).ToList();
                if (unknown.Count > 0 && context.Problems.Count < MaximumReportedProblems)
                {
                    context.Problems.Add($"{path}: sloupce {string.Join(", ", unknown)} tato verze Atlasu nezná. "
                        + "Obnovte zálohu verzí Atlasu, která ji vytvořila, a aktualizujte běžnou cestou.");
                }
            }
            context.Rows[ModuleId + "/" + table.Name] = count;
        }
        foreach (var part in m_parts)
        {
            await part.ValidateAsync(session, context, ct);
        }
    }

    private void ValidateFolders(RestoreContext context)
    {
        var folders = Tables.Select(x => Folder(ModuleId, x.Secure) + x.Name + "/")
            .Concat(m_parts.Where(x => x.Folder is not null).SelectMany(x => new[] { Folder(ModuleId, false) + x.Folder + "/", Folder(ModuleId, true) + x.Folder + "/" }))
            .ToList();
        foreach (var path in context.Reader.Files(Folder(ModuleId, false)).Concat(context.Reader.Files(Folder(ModuleId, true))))
        {
            if (!folders.Any(folder => path.StartsWith(folder, StringComparison.Ordinal)))
            {
                context.Problems.Add($"{path}: soubor, který tato verze Atlasu nezná.");
            }
        }
    }

    /// <summary>The rows of one table of a backup, decrypted.</summary>
    internal static IEnumerable<(string Path, JsonObject Row)> Rows(IBackupReader reader, string moduleId, BackupTable table)
    {
        foreach (var path in reader.Files(Folder(moduleId, table.Secure) + table.Name + "/"))
        {
            JsonObject row;
            try
            {
                if (table.Secure)
                {
                    row = CanonicalJson.Parse(reader.ReadSecure(path));
                }
                else
                {
                    using var stream = reader.Open(path);
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    row = CanonicalJson.Parse(buffer.ToArray());
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Neplatný JSON v záloze: " + path, ex);
            }
            foreach (var column in table.Retired ?? [])
            {
                row.Remove(column);
            }
            yield return (path, row);
        }
    }

    public async Task RestoreAsync(ISession session, RestoreContext context, CancellationToken ct)
    {
        if (!context.Reader.Manifest.Modules.ContainsKey(ModuleId))
        {
            return;
        }
        var schema = await TableSchema.ReadAsync(session, ct);
        var tables = Tables.Where(x => !x.Secure || context.Mode == RestoreMode.Full).ToDictionary(x => x.Name);
        foreach (var name in TableSchema.Order(tables.Keys, schema))
        {
            await RestoreRowsAsync(session, schema[name], tables[name], context, ct);
        }
        foreach (var part in m_parts)
        {
            await part.RestoreAsync(session, context, ct);
        }
    }

    private async Task RestoreRowsAsync(ISession session, TableInfo info, BackupTable table, RestoreContext context, CancellationToken ct)
    {
        // References to a table this restore leaves empty (the secure part without the key, operational tables) are cleared; a
        // row that cannot exist without one is left out and counted.
        var detached = info.References.Where(x => !context.RestoredTables.Contains(x.Target)).ToList();
        // A non-deferrable reference within the table is checked at the end of its statement: all its rows go in one.
        var oneStatement = info.References.Any(x => x.Target == info.Name && !x.Deferrable);
        var batch = new List<string>();
        var columns = new SortedSet<string>(StringComparer.Ordinal);
        var characters = 0;
        var skipped = 0;
        foreach (var (_, row) in Rows(context.Reader, ModuleId, table))
        {
            if (!Detach(info, table, row, detached))
            {
                skipped++;
                continue;
            }
            foreach (var (column, purpose) in table.Protected ?? new Dictionary<string, string>())
            {
                if (row[column]?.GetValue<string>() is { Length: > 0 } plain)
                {
                    row[column] = m_protection.CreateProtector(purpose).Protect(plain);
                }
                // A secret the backup could not carry (null: an empty value written by 3.1 and older, or a lost key ring on export)
                // comes back as no secret, so the restore goes on and the administrator enters it again, as the feature asks anyway.
                // A NOT NULL column says no secret with "", a nullable one with null (its readers unprotect any value that is not null).
                else if (!info.Columns[column].Nullable)
                {
                    row[column] = "";
                }
            }
            columns.UnionWith(row.Select(x => x.Key));
            var text = row.ToJsonString();
            batch.Add(text);
            characters += text.Length;
            if (!oneStatement && characters > BatchCharacters)
            {
                await InsertAsync(session, info, columns, batch, ct);
                batch.Clear();
                characters = 0;
            }
        }
        await InsertAsync(session, info, columns, batch, ct);
        foreach (var identity in info.Columns.Values.Where(x => x.Identity))
        {
            var name = TableSchema.Quote(info.Name);
            await session.CreateSQLQuery($"SELECT setval(pg_get_serial_sequence(:table, :column), coalesce(max({TableSchema.Quote(identity.Name)}), 0) + 1, false) FROM {name}")
                .SetParameter("table", name).SetParameter("column", identity.Name).UniqueResultAsync<long>(ct);
        }
        if (skipped > 0)
        {
            context.Warnings.Add($"{ModuleId}/{info.Name}: {skipped} řádků se neobnovilo, protože bez zabezpečené části zálohy nemají na co odkazovat.");
        }
    }

    private static bool Detach(TableInfo info, BackupTable table, JsonObject row, IReadOnlyList<TableReference> detached)
    {
        foreach (var reference in detached)
        {
            if (reference.Columns.All(x => row[x] is null))
            {
                continue;
            }
            var nullable = reference.Columns.Where(x => info.Columns[x].Nullable).ToList();
            if (nullable.Count == 0)
            {
                return false;
            }
            // A composite reference with one column null is not checked (MATCH SIMPLE).
            foreach (var column in nullable)
            {
                row[column] = null;
                foreach (var (coupled, value) in table.Cleared?.GetValueOrDefault(column) ?? new Dictionary<string, string>())
                {
                    row[coupled] = value;
                }
            }
        }
        return true;
    }

    private static async Task InsertAsync(ISession session, TableInfo info, IReadOnlySet<string> columns, List<string> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }
        var list = string.Join(", ", columns.Select(TableSchema.Quote));
        var name = TableSchema.Quote(info.Name);
        var overriding = info.Columns.Values.Any(x => x.Identity) ? " OVERRIDING SYSTEM VALUE" : "";
        await session.CreateSQLQuery($"INSERT INTO {name} ({list}){overriding} SELECT {list} FROM jsonb_populate_recordset(CAST(NULL AS {name}), CAST(:rows AS jsonb))")
            .SetParameter("rows", "[" + string.Join(",", batch) + "]").ExecuteUpdateAsync(ct);
    }

    public async Task RebuildAsync(ISession session, CancellationToken ct)
    {
        if (m_rebuild is not null)
        {
            await session.CreateSQLQuery(m_rebuild).ExecuteUpdateAsync(ct);
        }
        if (m_rebuildInCode is not null)
        {
            await m_rebuildInCode(session, ct);
        }
    }
}
