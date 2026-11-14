using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Platform.Backup;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Backups.Installation;

/// <summary>
/// The public name of every account (<c>.atlas/modules/identity/names/&lt;id&gt;.json</c>): the only account data outside the
/// secure part, as the 2.x archives and the Git commit authors already carry it. Without the backup key the accounts come back
/// from it as inactive placeholders, so history keeps its authors and every reference to an account stays valid.
/// </summary>
internal sealed class AccountNamesPart : IBackupPart
{
    private const string Prefix = ".atlas/modules/identity/names/";

    public string? Folder => "names";

    public async IAsyncEnumerable<BackupFile> ExportAsync(ISession session, BackupExport export, [EnumeratorCancellation] CancellationToken ct)
    {
        var changed = export.ChangedSince is { } since ? $"CAST(CAST(xmin AS text) AS bigint) >= {since}" : "true";
        var rows = await session.CreateSQLQuery($"SELECT CAST(\"Id\" AS text) AS id, CASE WHEN {changed} THEN \"DisplayName\" END AS name FROM \"UserAccount\" ORDER BY \"Id\"")
            .ListAsync<object[]>(ct);
        foreach (var row in rows)
        {
            var path = Prefix + (string)row[0] + ".json";
            if (row[1] is not string name)
            {
                yield return new BackupFile(path, null);
                continue;
            }
            var bytes = CanonicalJson.Write(new JsonObject { ["DisplayName"] = name, ["Id"] = (string)row[0] });
            yield return new BackupFile(path, () => new MemoryStream(bytes, false));
        }
    }

    public Task ValidateAsync(ISession session, RestoreContext context, CancellationToken ct)
    {
        if (context.Mode == RestoreMode.ContentOnly)
        {
            context.Rows["identity/names"] = Names(context.Reader).Count();
        }
        return Task.CompletedTask;
    }

    public async Task RestoreAsync(ISession session, RestoreContext context, CancellationToken ct)
    {
        if (context.Mode != RestoreMode.ContentOnly)
        {
            return;
        }
        foreach (var chunk in Names(context.Reader).Chunk(1000))
        {
            // Inactive and without a credential: nobody signs in as a placeholder; an account of this installation stays as it is.
            var created = await session.CreateSQLQuery("INSERT INTO \"UserAccount\"(\"Id\",\"Username\",\"DisplayName\",\"IsWikiAdmin\",\"IsActive\",\"SecurityStamp\") "
                    + "SELECT x.\"Id\", 'restored-' || replace(CAST(x.\"Id\" AS text), '-', ''), left(x.\"DisplayName\", 200), false, false, "
                    + "md5(CAST(random() AS text) || CAST(x.\"Id\" AS text)) FROM jsonb_to_recordset(CAST(:rows AS jsonb)) AS x(\"Id\" uuid, \"DisplayName\" text) "
                    + "ON CONFLICT DO NOTHING RETURNING \"Id\"").AddScalar("Id", NHibernateUtil.Guid)
                .SetParameter("rows", new JsonArray([.. chunk]).ToJsonString()).ListAsync<Guid>(ct);
            // A placeholder is an account like any other: its first revision and account.created, with no actor (a restore).
            foreach (var id in created)
            {
                await AccountHistory.RecordAsync(session, await session.LoadAsync<UserAccount>(id, ct), null, ct, created: true);
            }
        }
    }

    private static IEnumerable<JsonNode> Names(IBackupReader reader)
    {
        foreach (var path in reader.Files(Prefix))
        {
            using var stream = reader.Open(path);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var name = CanonicalJson.Parse(buffer.ToArray());
            if (name["Id"]?.GetValue<string>() is not { } id || !Guid.TryParse(id, out _) || name["DisplayName"]?.GetValue<string>() is null)
            {
                throw new InvalidDataException("Neplatné jméno účtu v záloze: " + path);
            }
            yield return name;
        }
    }
}

/// <summary>
/// The bytes of attachments and uploaded space images, stored once per content in <c>.atlas/blobs/&lt;sha256&gt;</c>: the rows
/// (Attachment, SpaceCover) say which file of the attachment storage holds which content, so a restore writes each file back
/// under its own id.
/// </summary>
internal sealed class AttachmentBlobsPart(AttachmentFiles files, ILogger logger) : IBackupPart
{
    internal const string Prefix = ".atlas/blobs/";
    private static readonly Regex Sha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly BackupTable AttachmentTable = new("Attachment");
    private static readonly BackupTable CoverTable = new("SpaceCover");

    public string? Folder => null;

    public async IAsyncEnumerable<BackupFile> ExportAsync(ISession session, BackupExport export, [EnumeratorCancellation] CancellationToken ct)
    {
        var changed = export.ChangedSince is { } since ? $"CAST(CAST(xmin AS text) AS bigint) >= {since}" : "true";
        var rows = await session.CreateSQLQuery($"SELECT \"Sha256\" AS sha256, \"Id\" AS id, {changed} AS changed FROM \"Attachment\" "
                + $"UNION ALL SELECT \"Sha256\", \"FileId\", {changed} FROM \"SpaceCover\" WHERE \"FileId\" IS NOT NULL AND \"Sha256\" IS NOT NULL ORDER BY 1, 2")
            .ListAsync<object[]>(ct);
        foreach (var blob in rows.GroupBy(x => (string)x[0]))
        {
            var held = blob.Select(x => (Guid)x[1]).FirstOrDefault(id => File.Exists(files.PathFor(id)));
            if (!Sha256.IsMatch(blob.Key) || held == Guid.Empty)
            {
                // One lost file must not stop the backup; the restore reports the attachment without its bytes.
                logger.LogWarning("No file holds the attachment content {Sha256}; the backup leaves it out", blob.Key);
                continue;
            }
            yield return new BackupFile(Prefix + blob.Key, blob.Any(x => (bool)x[2]) ? () => files.Open(held) : null);
        }
    }

    public Task ValidateAsync(ISession session, RestoreContext context, CancellationToken ct)
    {
        var missing = Files(context.Reader).Where(x => !Holds(context.Reader, x.Sha256)).Select(x => x.Sha256).Distinct().Count();
        if (missing > 0)
        {
            context.Warnings.Add($"Záloze chybí obsah {missing} příloh nebo obrázků prostorů (soubory chyběly už při zálohování); obnoví se bez něj.");
        }
        context.Rows["attachments/blobs"] = context.Reader.Files(Prefix).Count();
        return Task.CompletedTask;
    }

    public async Task RestoreAsync(ISession session, RestoreContext context, CancellationToken ct)
    {
        foreach (var (id, sha256) in Files(context.Reader).Where(x => Holds(context.Reader, x.Sha256)))
        {
            var path = files.PathFor(id);
            if (File.Exists(path))
            {
                await using var existing = File.OpenRead(path);
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, ct)) == sha256)
                {
                    continue;
                }
                throw new InvalidDataException("Úložiště příloh obsahuje jiný soubor se stejným identifikátorem; obnova potřebuje prázdnou instalaci.");
            }
            await using var input = context.Reader.Open(Prefix + sha256);
            var written = await files.WriteAsync(id, input, false, ct);
            context.CreatedFiles.Add(path);
            if (written.Hash != sha256)
            {
                throw new InvalidDataException("Obsah přílohy se při obnově změnil.");
            }
        }
    }

    /// <summary>Which file of the attachment storage holds which content, from the rows of the backup.</summary>
    private static IEnumerable<(Guid Id, string Sha256)> Files(IBackupReader reader)
    {
        foreach (var (_, row) in TableBackup.Rows(reader, "attachments", AttachmentTable))
        {
            yield return (Guid.Parse(row["Id"]!.GetValue<string>()), row["Sha256"]!.GetValue<string>());
        }
        foreach (var (_, row) in TableBackup.Rows(reader, "platform", CoverTable))
        {
            if (row["FileId"]?.GetValue<string>() is { } file && row["Sha256"]?.GetValue<string>() is { } sha256)
            {
                yield return (Guid.Parse(file), sha256);
            }
        }
    }

    private static bool Holds(IBackupReader reader, string sha256)
        => reader.Manifest.Files.TryGetValue(Prefix + sha256, out var actual) && actual == sha256;
}

/// <summary>
/// The KDBX files of vaults in a managed location (the folder Atlas keeps under <c>Vaults:DataPath</c>), encrypted under
/// <c>.atlas/secure/vaults/files/&lt;vault&gt;.kdbx.enc</c>. Files of local and SMB locations live outside Atlas and are not copied.
/// </summary>
internal sealed class VaultFilesPart(VaultFolders folders) : IBackupPart
{
    private const string Prefix = ".atlas/secure/vaults/files/";
    private static readonly BackupTable VaultTable = new("Vault", true);
    private static readonly BackupTable LocationTable = new("VaultLocation", true);

    public string? Folder => "files";

    public async IAsyncEnumerable<BackupFile> ExportAsync(ISession session, BackupExport export, [EnumeratorCancellation] CancellationToken ct)
    {
        if (export.Key is not { } key)
        {
            yield break;
        }
        var rows = await session.CreateSQLQuery("SELECT CAST(v.\"Id\" AS text) AS id, v.\"LocationId\" AS location, v.\"FileName\" AS file FROM \"Vault\" v "
                + "JOIN \"VaultLocation\" l ON l.\"Id\" = v.\"LocationId\" WHERE l.\"Kind\" = 'managed' ORDER BY v.\"Id\"")
            .ListAsync<object[]>(ct);
        foreach (var row in rows)
        {
            var file = Path.Combine(folders.Managed((Guid)row[1]), (string)row[2]);
            if (!File.Exists(file))
            {
                continue;
            }
            var path = Prefix + (string)row[0] + ".kdbx.enc";
            // Small files, encrypted deterministically: an unchanged vault rewrites the same bytes, so nothing is committed.
            var bytes = key.Encrypt(path, await File.ReadAllBytesAsync(file, ct));
            yield return new BackupFile(path, () => new MemoryStream(bytes, false));
        }
    }

    public Task ValidateAsync(ISession session, RestoreContext context, CancellationToken ct)
    {
        if (context.Mode == RestoreMode.Full)
        {
            // Decrypting every file proves the key and the files before anything is written.
            context.Rows["vaults/files"] = Targets(context.Reader).Count();
        }
        return Task.CompletedTask;
    }

    public async Task RestoreAsync(ISession session, RestoreContext context, CancellationToken ct)
    {
        if (context.Mode != RestoreMode.Full)
        {
            return;
        }
        foreach (var (file, data) in Targets(context.Reader))
        {
            if (File.Exists(file))
            {
                if ((await File.ReadAllBytesAsync(file, ct)).AsSpan().SequenceEqual(data))
                {
                    continue;
                }
                throw new InvalidDataException("Složka trezorů obsahuje jiný soubor trezoru; obnova potřebuje prázdnou instalaci.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, data, ct);
            context.CreatedFiles.Add(file);
        }
    }

    /// <summary>Where each backed-up vault file belongs and its decrypted bytes.</summary>
    private IEnumerable<(string File, byte[] Data)> Targets(IBackupReader reader)
    {
        var locations = TableBackup.Rows(reader, "vaults", LocationTable).ToDictionary(x => x.Row["Id"]!.GetValue<string>(), x => x.Row["Kind"]!.GetValue<string>());
        foreach (var (_, vault) in TableBackup.Rows(reader, "vaults", VaultTable))
        {
            var path = Prefix + vault["Id"]!.GetValue<string>() + ".kdbx.enc";
            var location = vault["LocationId"]!.GetValue<string>();
            var name = vault["FileName"]!.GetValue<string>();
            if (!reader.Manifest.Files.ContainsKey(path) || locations.GetValueOrDefault(location) != "managed")
            {
                continue;
            }
            if (name != Path.GetFileName(name) || name is "." or "..")
            {
                throw new InvalidDataException("Neplatný název souboru trezoru v záloze.");
            }
            yield return (Path.Combine(folders.Managed(Guid.Parse(location)), name), reader.ReadSecure(path));
        }
    }
}
