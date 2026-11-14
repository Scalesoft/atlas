using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlas.Server.Platform.Backup;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Backups.Installation;

/// <summary>Where the export writes: a ZIP archive or the Git working tree.</summary>
internal interface IBackupSink
{
    /// <summary>The key the sink's current files are encrypted with; another key rewrites every secure file.</summary>
    string? KeyId { get; }

    /// <summary>The SHA-256 of a file the sink already holds from the previous export, or null.</summary>
    string? Held(string path);

    /// <summary>Writes the file and returns its SHA-256.</summary>
    Task<string> WriteAsync(string path, Stream content, CancellationToken ct);

    /// <summary>Writes the manifest; a sink that keeps files between exports removes those the manifest no longer lists.</summary>
    Task CompleteAsync(BackupManifest manifest, CancellationToken ct);
}

/// <summary>The restorable part of the backup tree (format v7: <c>.atlas/manifest.json</c>, <c>.atlas/modules</c>,
/// <c>.atlas/blobs</c>, <c>.atlas/secure</c>) from one snapshot of the database, for every module of the distribution.</summary>
public sealed class InstallationExport(BackupCatalog catalog)
{
    internal static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public BackupCatalog Catalog => catalog;

    /// <summary>
    /// Exports within the caller's read-only repeatable-read transaction. <paramref name="changedSince"/> is what the previous export
    /// returned: rows older than it are taken from the sink as they are. Returns the value for the next export.
    /// </summary>
    internal async Task<long> WriteAsync(ISession session, IBackupSink sink, BackupKey? key, long? changedSince, CancellationToken ct)
    {
        await session.CreateSQLQuery("SET LOCAL TimeZone = 'UTC'").ExecuteUpdateAsync(ct);
        // Every transaction below the snapshot's xmin has finished: a row with an older xmin was in the previous export.
        var horizon = await session.CreateSQLQuery("SELECT CAST(CAST(pg_snapshot_xmin(pg_current_snapshot()) AS text) AS bigint)").UniqueResultAsync<long>(ct);
        var schema = await session.CreateSQLQuery("SELECT coalesce(max(\"Version\"), 0) FROM \"SchemaVersion\"").UniqueResultAsync<long>(ct);
        // A row carries the 32-bit xid; after the counter wraps into a new epoch everything is compared once.
        var since = changedSince is { } previous && previous >> 32 == horizon >> 32 && sink.KeyId == key?.Id ? previous & 0xFFFFFFFF : (long?)null;
        var files = await FilesAsync(session, sink, key, since, ct) ?? (await FilesAsync(session, sink, key, null, ct))!;
        var modules = new SortedDictionary<string, int>(catalog.Modules.ToDictionary(x => x.ModuleId, x => x.FormatVersion), StringComparer.Ordinal);
        await sink.CompleteAsync(new BackupManifest(BackupManifest.FormatName, BackupManifest.CurrentVersion, schema, key?.Id, modules, files), ct);
        return horizon;
    }

    /// <summary>Null when the sink lacks a file the export reports unchanged: the caller exports everything instead.</summary>
    private async Task<SortedDictionary<string, string>?> FilesAsync(ISession session, IBackupSink sink, BackupKey? key, long? since, CancellationToken ct)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in catalog.Modules)
        {
            await foreach (var file in module.ExportAsync(session, new BackupExport(key, since), ct))
            {
                if (file.Open is null)
                {
                    if (sink.Held(file.Path) is not { } held)
                    {
                        return null;
                    }
                    files[file.Path] = held;
                    continue;
                }
                await using var content = file.Open();
                files[file.Path] = await sink.WriteAsync(file.Path, content, ct);
            }
        }
        return files;
    }

    /// <summary>
    /// A cheap stand-in for <see cref="ContentTokenAsync"/>: the writes to the backed-up tables PostgreSQL has counted (its cumulative
    /// statistics, no table scan); null where it does not count them (track_counts off). A transaction's writes are counted shortly
    /// after it ends and never before, so read it before the snapshot it stands for: an unchanged value then means no backed-up row
    /// was written since; a changed one may be a false alarm (a rolled-back write) that the token settles.
    /// </summary>
    internal async Task<long?> WatermarkAsync(ISession session, CancellationToken ct)
        => await session.CreateSQLQuery("""
                SELECT CASE WHEN current_setting('track_counts') = 'on' THEN CAST(coalesce(sum(n_tup_ins + n_tup_upd + n_tup_del), 0) AS bigint) END AS "Watermark"
                FROM pg_stat_user_tables WHERE schemaname = current_schema() AND relname IN (:tables)
                """).AddScalar("Watermark", NHibernate.NHibernateUtil.Int64)
            .SetParameterList("tables", catalog.Modules.SelectMany(module => module.Tables).Select(table => table.Name).ToList())
            .UniqueResultAsync<long?>(ct);

    /// <summary>Changes whenever a backed-up row, the key or the schema changes: an archive with the same token holds the same data.</summary>
    internal async Task<string> ContentTokenAsync(ISession session, BackupKey? key, CancellationToken ct)
    {
        var text = new StringBuilder(key?.Id ?? "-").Append('|')
            .Append(await session.CreateSQLQuery("SELECT coalesce(max(\"Version\"), 0) FROM \"SchemaVersion\"").UniqueResultAsync<long>(ct));
        foreach (var module in catalog.Modules)
        {
            text.Append('|').Append(module.ModuleId).Append(':').Append(module.FormatVersion).Append(':').Append(await module.ChangeStampAsync(session, ct));
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    internal static byte[] Serialize(BackupManifest manifest) => CanonicalJson.Write(JsonSerializer.SerializeToNode(manifest, ManifestJson)!);

    internal const string Readme = """
        # Atlas – záloha instalace

        Tento strom zapisuje Atlas (formát 7, docs/backup-format-v7.md). Neupravujte ho ručně.

        - `.atlas/manifest.json` – verze formátu, moduly a jejich verze, SHA-256 každého souboru obnovitelné části.
        - `.atlas/modules/<modul>/<Tabulka>/<klíč>.json` – bezztrátová data modulů, jeden řádek databáze na soubor.
        - `.atlas/blobs/<sha256>` – přílohy a obrázky, každý obsah jednou.
        - `.atlas/secure/<modul>/...enc` – účty, granty, trezory a tajemství, šifrované klíčem zálohy (AES-GCM).
          Klíč zobrazí Atlas jednou při vytvoření; bez něj jde obnovit obsah, ale ne účty, oprávnění a tajemství.

        Obnova: Správa wiki → Provoz → Zálohy → Obnova instalace (ZIP nebo repozitář Git) do prázdné instalace.

        """;
}
