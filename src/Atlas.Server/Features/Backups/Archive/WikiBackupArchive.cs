using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.SpaceCovers.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Backups.Archive;

public sealed class BackupArchiveFile(string path, string contentToken) : IDisposable
{
    public string Path { get; } = path;
    public string ContentToken { get; } = contentToken;
    public void Dispose() => File.Delete(Path);
}

/// <summary>Builds a standalone saved-content archive from one repeatable-read snapshot. Blobs are immutable and streamed.</summary>
public sealed class WikiBackupArchive(AtlasDatabase database, AttachmentFiles attachments)
{
    public async Task<string> GetContentTokenAsync(string mode, CancellationToken ct)
    {
        BackupFormat.RequireMode(mode);
        using var session = database.OpenSession();
        using var transaction = session.BeginTransaction(IsolationLevel.RepeatableRead);
        var token = await ContentTokenAsync(session, mode, ct);
        await transaction.CommitAsync(ct);
        return token;
    }

    private static async Task<string> ContentTokenAsync(ISession session, string mode, CancellationToken ct)
    {
        var sql = "SELECT r.\"Id\",r.\"Version\" FROM \"PageRevision\" r JOIN \"Page\" p ON p.\"Id\"=r.\"PageId\"";
        // A purged page (a tombstone in the trash) is in no archive: a restore must never bring it back.
        sql += mode == "latest" ? " WHERE NOT p.\"IsTrashed\"" : " WHERE p.\"PurgedAt\" IS NULL";
        sql += " ORDER BY r.\"Id\"";
        var revisions = await session.CreateSQLQuery(sql).SetMaxResults(BackupFormat.MaximumEntries + 1).ListAsync<object[]>(ct);
        if (revisions.Count > BackupFormat.MaximumEntries)
        {
            throw new InvalidDataException("Historie wiki překračuje limit zálohy.");
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(mode + "\n"));
        foreach (var row in revisions)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{row[0]:D}:{row[1]}\n"));
        }
        var flaggedSql = "SELECT \"Id\" FROM \"Page\" WHERE \"IsTemplate\"" + (mode == "latest" ? " AND NOT \"IsTrashed\"" : " AND \"PurgedAt\" IS NULL") + " ORDER BY \"Id\"";
        var flagged = await session.CreateSQLQuery(flaggedSql).SetMaxResults(10001).ListAsync<Guid>(ct);
        if (flagged.Count > 10000)
            throw new InvalidDataException("Záloha může obsahovat nejvýše 10 000 stránek.");
        foreach (var id in flagged)
            hash.AppendData(Encoding.UTF8.GetBytes($"template:{id:D}\n"));
        // A changed image is a reason for a new archive; without images the token stays what it was before format 5. The line of
        // an upload is the one format 5 wrote, so a wiki without a chosen drawing keeps its token.
        foreach (var cover in await CurrentCoversAsync(session, ct))
            hash.AppendData(Encoding.UTF8.GetBytes(cover.Source == SpaceCoverSource.Upload
                ? $"cover:{cover.SpaceId:D}:{cover.Sha256}:{cover.Tinted}\n"
                : $"cover:{cover.SpaceId:D}:{cover.Source}:{cover.CatalogKey}\n"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The current images a person set (an upload, a chosen drawing, no image by choice), one per space at most. The
    /// automatic image is not archived: it is computed again wherever the space is read.</summary>
    private static async Task<IList<SpaceCover>> CurrentCoversAsync(ISession session, CancellationToken ct) =>
        await session.QueryOver<SpaceCover>().Where(x => x.ActiveTo == null && x.Source != SpaceCoverSource.Auto).OrderBy(x => x.SpaceId).Asc.ListAsync(ct);

    public async Task<BackupArchiveFile> CreateAsync(string mode, CancellationToken ct)
    {
        BackupFormat.RequireMode(mode);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atlas-backup-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var session = database.OpenSession();
            session.DefaultReadOnly = true;
            using var transaction = session.BeginTransaction(IsolationLevel.RepeatableRead);
            var token = await ContentTokenAsync(session, mode, ct);
            await RequireContentBudgetAsync(session, mode, ct);
            var pages = await ReadPagesAsync(session, mode, ct);
            var pageIds = pages.Select(x => x.Id).ToHashSet();
            var spaces = session.QueryOver<Space>();
            // Like trashed pages, a trashed space is in a full archive only, with its trash state in trash.json.
            spaces = mode == "latest" ? spaces.Where(x => x.TrashedAt == null) : spaces.Where(x => x.PurgedAt == null);
            var spaceRows = await spaces.OrderBy(x => x.Id).Asc.Take(10001).ListAsync(ct);
            if (spaceRows.Count > 10000)
            {
                throw new InvalidDataException("Záloha obsahuje příliš mnoho prostorů.");
            }
            var allAssets = new List<BackupAttachment>();
            var authors = new Dictionary<Guid, string>();
            var covers = (await CurrentCoversAsync(session, ct)).ToDictionary(x => x.SpaceId);
            await using var output = BackupFormat.CreatePrivateFile(path);
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                var writer = new BackupZipWriter(zip);
                foreach (var space in spaceRows)
                {
                    await writer.JsonAsync($"spaces/{space.Id:D}/space.json", space, ct);
                    if (space is { TrashedAt: { } trashedAt, TrashBatchId: { } batch })
                    {
                        await writer.JsonAsync(BackupFormat.TrashMetadataPath(space.Id), new BackupSpaceTrash(trashedAt, batch), ct);
                    }
                    if (covers.TryGetValue(space.Id, out var cover))
                    {
                        await WriteCoverAsync(writer, cover, ct);
                    }
                }
                bool hasSummaries = false, hasVersions = false;
                foreach (var page in pages)
                {
                    var written = await WritePageAsync(session, writer, page, pageIds, mode, allAssets, authors, ct);
                    hasSummaries |= written.Summaries;
                    hasVersions |= written.Versions;
                }
                var wikiName = await session.CreateSQLQuery("SELECT \"Name\" FROM \"Workspace\" WHERE \"Id\"=1").UniqueResultAsync<string>(ct) ?? "Atlas";
                await writer.JsonAsync("wiki.json", new BackupWiki(wikiName, spaceRows.Select(x => x.Id).ToList(), pages.Select(x => x.Id).ToList(),
                    allAssets.Select(x => x.Attachment.Id).ToList(), authors), ct);
                await writer.TextAsync("README.md", """
                    # Atlas content backup

                    This standalone ZIP contains saved wiki content. Open pages/<UUID>/content.md with a Markdown reader;
                    page links and attachment links are relative. Binary files keep a portable form of their original names.
                    content.json contains the unmodified native rich document. page.json records original Markdown,
                    revision times, author identifiers and publication history; wiki.json names historical authors.

                    FULL includes all saved page revisions and trash. LATEST includes the current draft and a separately
                    published revision, and excludes trash. Unsaved live collaboration edits are not a saved revision.
                    Accounts, credentials, access grants, integrations and operational settings are not restored.
                    A page with its own access (ownAccess in page.json) is restored restricted: only space administrators
                    and wiki administrators see it until its access is granted again.
                    spaces/<UUID>/cover.json is the image of the space: an uploaded image with its file next to it, or
                    a chosen built-in drawing or the choice of no image (source, catalogKey) without a file. A space
                    without cover.json gets its image automatically.
                    spaces/<UUID>/trash.json (full archives only) says the space is in the trash: trashedAt and the
                    trashBatchId its pages trashed with it share.
                    Importing alongside creates new UUID addresses and adds a visible -restore- suffix to names.
                    Restoring existing pages keeps their identity and appends the backed-up revision chain to existing history.
                    SHA-256 checksums in manifest.json cover every other file; they detect corruption, not malicious authorship.
                    """, ct);
                var version = hasVersions ? BackupFormat.RevisionVersionsVersion
                    : spaceRows.Any(x => x.IsTrashed) ? BackupFormat.SpaceTrashVersion
                    : covers.Values.Any(x => x.Source != SpaceCoverSource.Upload) ? BackupFormat.CoverChoiceVersion
                    : covers.Count > 0 ? BackupFormat.CoverVersion
                    : hasSummaries ? BackupFormat.ChangeSummaryVersion
                    : pages.Any(x => x.IsTemplate) ? BackupFormat.TemplateVersion
                    : pages.Any(x => x.OwnAccess) ? BackupFormat.ItemAccessVersion : BackupFormat.Version;
                await writer.ManifestAsync(new BackupManifest("scaleatlas-wiki-backup", version, mode, DateTime.UtcNow, token, writer.Entries), ct);
            }
            await output.FlushAsync(ct);
            if (output.Length > BackupFormat.MaximumZipBytes)
            {
                throw new InvalidDataException("Komprimovaná záloha překračuje 1 GiB.");
            }
            await transaction.CommitAsync(ct);
            return new BackupArchiveFile(path, token);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }
    /// <summary>Writes the page; whether one of its archived publications carries a change summary (the archive is then format 4) and
    /// whether one of its revisions carries version columns (format 8).</summary>
    private async Task<(bool Summaries, bool Versions)> WritePageAsync(ISession session, BackupZipWriter writer, Page page, HashSet<Guid> pageIds,
        string mode, List<BackupAttachment> allAssets, Dictionary<Guid, string> authors, CancellationToken ct)
    {
        var revisions = await ReadRevisionsAsync(session, page, mode, ct);
        var revisionIds = revisions.Select(x => x.Id).ToHashSet();
        var bindings = await session.QueryOver<PageRevisionAttachment>().Where(x => x.PageId == page.Id).ListAsync(ct);
        var references = bindings.Where(x => revisionIds.Contains(x.RevisionId)).Select(x => x.AttachmentId).ToHashSet();
        var assets = await session.QueryOver<Attachment>().Where(x => x.PageId == page.Id).ListAsync(ct);
        if (mode == "latest")
        {
            assets = assets.Where(x => x.ActiveTo is null || references.Contains(x.Id)).ToList();
        }
        var filePaths = assets.ToDictionary(x => x.Id, BackupFormat.BinaryPath);
        await WriteAttachmentsAsync(writer, assets, filePaths, allAssets, ct);
        var publications = await session.QueryOver<PagePublication>().Where(x => x.PageId == page.Id)
            .OrderBy(x => x.ActiveFrom).Asc.ListAsync(ct);
        publications = publications.Where(x => revisionIds.Contains(x.RevisionId) && (mode == "full" || x.ActiveTo is null)).ToList();
        var metadataRevisions = revisions.Select(r => new BackupRevision(r.Id, r.Version, r.Title, r.AuthorId,
            r.ActiveFrom, r.ActiveTo, r.Markdown, bindings.Where(x => x.RevisionId == r.Id).Select(x => x.AttachmentId).Order().ToList(),
            r.GroupId == r.Id && r.CheckpointCount == 1 && !r.IsExplicit && r.Label is null && r.ChangeSummary is null ? null
                : new BackupRevisionVersion(r.GroupId, r.GroupStartedAt, r.CheckpointCount, r.IsExplicit, r.Label, r.BaseRevisionId, r.ChangeSummary))).ToList();
        var summaries = publications.Where(x => x.ChangeSummary is not null).ToDictionary(x => x.Id, x => x.ChangeSummary!);
        var directory = $"pages/{page.Id:D}";
        // A latest archive can omit a trashed ancestor. Detach that parent in the portable snapshot.
        if (page.ParentId is { } parent && !pageIds.Contains(parent))
        {
            page.ParentId = null;
        }
        await writer.JsonAsync(directory + "/page.json", new BackupPage(page, metadataRevisions, publications.ToList(), page.OwnAccess, page.IsTemplate, summaries.Count == 0 ? null : summaries), ct);
        await WriteRevisionFilesAsync(session, writer, revisions, directory, pageIds, filePaths, authors, ct);
        return (summaries.Count > 0, metadataRevisions.Any(x => x.Group is not null));
    }

    private static async Task<IList<PageRevision>> ReadRevisionsAsync(ISession session, Page page, string mode, CancellationToken ct)
    {
        var revisionQuery = session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id);
        if (mode == "latest")
        {
            revisionQuery = revisionQuery.And(x => x.ActiveTo == null || x.Id == page.PublishedRevisionId);
        }
        var revisions = await revisionQuery.OrderBy(x => x.Version).Asc.Take(25001).ListAsync(ct);
        if (revisions.Count is 0 or > 25000 || revisions.Count(x => x.ActiveTo is null) != 1)
        {
            throw new InvalidDataException("Stránka nemá platnou aktuální verzi nebo překračuje limit historie.");
        }
        return revisions;
    }

    private async Task WriteAttachmentsAsync(BackupZipWriter writer, IList<Attachment> assets,
        Dictionary<Guid, string> filePaths, List<BackupAttachment> allAssets, CancellationToken ct)
    {
        foreach (var asset in assets)
        {
            var metadata = new BackupAttachment(asset, filePaths[asset.Id]);
            allAssets.Add(metadata);
            await writer.JsonAsync($"attachments/{asset.Id:D}/attachment.json", metadata, ct);
            await using var source = attachments.Open(asset.Id);
            var entry = await writer.StreamAsync(metadata.File, source, ct);
            if (entry.Size != asset.Size || entry.Sha256 != asset.Sha256)
            {
                throw new InvalidDataException("Soubor přílohy nesouhlasí s uloženou kontrolní sumou.");
            }
        }
    }

    private async Task WriteCoverAsync(BackupZipWriter writer, SpaceCover cover, CancellationToken ct)
    {
        if (cover.Source != SpaceCoverSource.Upload)
        {
            // Format 6: a chosen drawing or no image by choice travels without a file.
            await writer.JsonAsync(BackupFormat.CoverMetadataPath(cover.SpaceId), new BackupSpaceCover(Source: cover.Source.ToString(), CatalogKey: cover.CatalogKey), ct);
            return;
        }
        var file = BackupFormat.CoverPath(cover.SpaceId, cover.MimeType!)!;
        var metadata = new BackupSpaceCover(file, cover.MimeType!, cover.Size!.Value, cover.Sha256!, cover.Tinted);
        await writer.JsonAsync(BackupFormat.CoverMetadataPath(cover.SpaceId), metadata, ct);
        await using var source = OpenCover(cover.FileId!.Value);
        var entry = await writer.StreamAsync(file, source, ct);
        if (entry.Size != metadata.Size || entry.Sha256 != metadata.Sha256)
        {
            throw new InvalidDataException("Úvodní obrázek prostoru nesouhlasí s uloženou kontrolní sumou.");
        }
    }

    private FileStream OpenCover(Guid fileId)
    {
        try
        {
            return attachments.Open(fileId);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // Unlike attachment blobs, a replaced image's file is deleted once its change commits; the next run sees the new one.
            throw new InvalidDataException("Úvodní obrázek prostoru chybí nebo se během zálohy změnil.", e);
        }
    }

    private static async Task WriteRevisionFilesAsync(ISession session, BackupZipWriter writer, IList<PageRevision> revisions,
        string directory, HashSet<Guid> pageIds, Dictionary<Guid, string> filePaths, Dictionary<Guid, string> authors, CancellationToken ct)
    {
        foreach (var revision in revisions)
        {
            if (!authors.ContainsKey(revision.AuthorId))
            {
                var author = await session.GetAsync<UserAccount>(revision.AuthorId, ct);
                authors[revision.AuthorId] = author?.DisplayName ?? revision.AuthorId.ToString();
            }
            var history = directory + "/history/" + revision.Version;
            await writer.TextAsync(history + "/content.json", revision.ContentJson, ct);
            await writer.TextAsync(history + "/content.md", BackupFormat.PortableMarkdown(revision.Markdown, history, pageIds, filePaths), ct);
            if (revision.ActiveTo is null)
            {
                await writer.TextAsync(directory + "/content.json", revision.ContentJson, ct);
                await writer.TextAsync(directory + "/content.md", BackupFormat.PortableMarkdown(revision.Markdown, directory, pageIds, filePaths), ct);
            }
            await session.EvictAsync(revision, ct);
        }
    }

    private static async Task RequireContentBudgetAsync(ISession session, string mode, CancellationToken ct)
    {
        var contentSizeSql = "SELECT CAST(COALESCE(sum(CAST(octet_length(r.\"ContentJson\") AS bigint)+octet_length(r.\"Markdown\")),0) AS bigint) " +
            "FROM \"PageRevision\" r JOIN \"Page\" p ON p.\"Id\"=r.\"PageId\"";
        if (mode == "latest")
        {
            contentSizeSql += " WHERE NOT p.\"IsTrashed\" AND (r.\"ActiveTo\" IS NULL OR r.\"Id\"=p.\"PublishedRevisionId\")";
        }
        else
        {
            contentSizeSql += " WHERE p.\"PurgedAt\" IS NULL";
        }
        if (await session.CreateSQLQuery(contentSizeSql).UniqueResultAsync<long>(ct) > BackupFormat.MaximumMetadataBytes)
        {
            throw new InvalidDataException("Textový obsah wiki překračuje limit zálohy.");
        }
    }

    private static async Task<IList<Page>> ReadPagesAsync(ISession session, string mode, CancellationToken ct)
    {
        var query = mode == "latest" ? session.QueryOver<Page>().Where(x => !x.IsTrashed) : session.QueryOver<Page>().Where(x => x.PurgedAt == null);
        var pages = await query.OrderBy(x => x.Id).Asc.Take(10001).ListAsync(ct);
        if (pages.Count > 10000)
        {
            throw new InvalidDataException("Záloha může obsahovat nejvýše 10 000 stránek.");
        }
        return pages;
    }
}

internal sealed class BackupZipWriter(ZipArchive zip)
{
    private long m_size;
    private long m_metadataSize;
    public List<BackupEntry> Entries { get; } = [];

    internal Task JsonAsync<T>(string path, T value, CancellationToken ct) => TextAsync(path, JsonSerializer.Serialize(value, BackupFormat.Json), ct);

    internal async Task TextAsync(string path, string value, CancellationToken ct)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(value), false);
        await StreamAsync(path, input, ct);
    }

    internal async Task<BackupEntry> StreamAsync(string path, Stream input, CancellationToken ct)
    {
        if (Entries.Count >= BackupFormat.MaximumEntries - 1)
        {
            throw new InvalidDataException("Záloha obsahuje příliš mnoho souborů.");
        }
        BackupFormat.RequirePath(path);
        await using var output = zip.CreateEntry(path, CompressionLevel.Fastest).Open();
        var entry = await BackupFormat.CopyAsync(input, output, path, Math.Min(BackupFormat.MaximumFileBytes, BackupFormat.MaximumExpandedBytes - m_size), ct);
        m_size += entry.Size;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            m_metadataSize += entry.Size;
            if (m_metadataSize > BackupFormat.MaximumMetadataBytes)
            {
                throw new InvalidDataException("Textová data zálohy překračují 256 MiB.");
            }
        }
        Entries.Add(entry);
        return entry;
    }

    internal async Task ManifestAsync(BackupManifest manifest, CancellationToken ct)
    {
        var text = JsonSerializer.Serialize(manifest, BackupFormat.Json);
        if (m_metadataSize + Encoding.UTF8.GetByteCount(text) > BackupFormat.MaximumMetadataBytes)
        {
            throw new InvalidDataException("Textová data zálohy překračují 256 MiB.");
        }
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text), false);
        await using var output = zip.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
        await BackupFormat.CopyAsync(input, output, "manifest.json", BackupFormat.MaximumFileBytes, ct);
    }
}
