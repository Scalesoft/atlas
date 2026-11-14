using System.IO.Compression;
using System.Text.Json;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.SpaceCovers.Domain;
using Atlas.Markdown.Sync;

namespace Atlas.Server.Features.Backups.Archive;

/// <summary>Validates and stages every entry before any database or attachment-storage mutation.</summary>
internal sealed class BackupArchiveReader : IDisposable
{
    private readonly string m_directory = Path.Combine(Path.GetTempPath(), "atlas-backup-read-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, (string File, BackupEntry Entry)> m_files = new(StringComparer.OrdinalIgnoreCase);
    internal BackupManifest Manifest { get; private set; } = null!;
    internal BackupWiki Wiki { get; private set; } = null!;
    internal List<Space> Spaces { get; } = [];
    /// <summary>Format 5 and 6: uploaded images and image choices by the archived space id.</summary>
    internal Dictionary<Guid, BackupSpaceCover> Covers { get; } = [];
    internal Dictionary<Guid, BackupSpaceTrash> Trash { get; } = [];
    internal List<BackupPage> Pages { get; } = [];
    internal List<BackupAttachment> Attachments { get; } = [];
    /// <summary>Revisions whose content the server would refuse on any other write (<see cref="Rules.ValidateDocument(string?)"/>).
    /// The import refuses the archive with their list.</summary>
    internal List<BackupDocumentIssue> InvalidDocuments { get; } = [];
    /// <summary>Repairs of Atlas 2.x shapes (<see cref="LegacyDocumentRepair"/>), one per change; the import reads every revision repaired.</summary>
    internal List<BackupDocumentIssue> RepairedDocuments { get; } = [];

    internal static async Task<BackupArchiveReader> ReadAsync(Stream input, CancellationToken ct)
    {
        var reader = new BackupArchiveReader();
        Directory.CreateDirectory(reader.m_directory);
        try
        {
            await reader.StageAsync(input, ct);
            await reader.ValidateAsync(ct);
            return reader;
        }
        catch (JsonException ex)
        {
            reader.Dispose();
            throw new InvalidDataException("Záloha obsahuje neplatný JSON.", ex);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    internal string FileFor(string path) => m_files.TryGetValue(path, out var file) ? file.File : throw new InvalidDataException("V záloze chybí soubor: " + path);
    /// <summary>The revision's content as the import stores it: Atlas 2.x shapes repaired (<see cref="LegacyDocumentRepair"/>).</summary>
    internal async Task<string> ContentAsync(Guid page, int version, CancellationToken ct, ICollection<LegacyDocumentChange>? changes = null) =>
        LegacyDocumentRepair.Repair(await File.ReadAllTextAsync(FileFor($"pages/{page:D}/history/{version}/content.json"), ct), changes);

    private async Task StageAsync(Stream input, CancellationToken ct)
    {
        var compressed = Path.Combine(m_directory, "archive.zip");
        await using var spool = BackupFormat.CreatePrivateFile(compressed);
        await BackupFormat.CopyAsync(input, spool, "archive.zip", BackupFormat.MaximumZipBytes, ct);
        spool.Position = 0;
        using var zip = new ZipArchive(spool, ZipArchiveMode.Read, true);
        if (zip.Entries.Count > BackupFormat.MaximumEntries)
        {
            throw new InvalidDataException("ZIP obsahuje příliš mnoho položek.");
        }
        long expanded = 0;
        long metadata = 0;
        foreach (var entry in zip.Entries)
        {
            BackupFormat.RequirePath(entry.FullName);
            if (m_files.ContainsKey(entry.FullName) || entry.Length > BackupFormat.MaximumFileBytes ||
                expanded + entry.Length > BackupFormat.MaximumExpandedBytes || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
            {
                throw new InvalidDataException("ZIP obsahuje duplicitní položku, symbolický odkaz nebo příliš velký soubor.");
            }
            if (entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                metadata += entry.Length;
                if (metadata > BackupFormat.MaximumMetadataBytes)
                {
                    throw new InvalidDataException("Textová data zálohy překračují 256 MiB.");
                }
            }
            expanded += await StageEntryAsync(entry, expanded, ct);
        }
    }

    private async Task<long> StageEntryAsync(ZipArchiveEntry entry, long expanded, CancellationToken ct)
    {
        var path = Path.Combine(m_directory, m_files.Count.ToString("D8") + ".entry");
        await using var target = BackupFormat.CreatePrivateFile(path);
        await using var source = entry.Open();
        var verified = await BackupFormat.CopyAsync(source, target, entry.FullName,
            Math.Min(BackupFormat.MaximumFileBytes, BackupFormat.MaximumExpandedBytes - expanded), ct);
        if (verified.Size != entry.Length)
        {
            throw new InvalidDataException("Velikost souboru v ZIP nesouhlasí.");
        }
        m_files.Add(entry.FullName, (path, verified));
        return verified.Size;
    }

    private async Task<T> JsonAsync<T>(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(FileFor(path));
        return await JsonSerializer.DeserializeAsync<T>(input, BackupFormat.Json, ct) ?? throw new InvalidDataException("Prázdná metadata zálohy.");
    }

    private async Task ValidateAsync(CancellationToken ct)
    {
        await ReadManifestAsync(ct);
        await ReadWikiAsync(ct);
        var expected = new HashSet<string>(StringComparer.Ordinal) { "manifest.json", "README.md", "wiki.json" };
        await ReadSpacesAsync(expected, ct);
        await ReadAttachmentsAsync(expected, ct);
        await ReadPagesAsync(expected, ct);
        ValidateParents();
        if (!expected.SetEquals(m_files.Keys))
        {
            throw new InvalidDataException("Záloha obsahuje neznámé soubory nebo některé povinné soubory chybí.");
        }
    }

    private async Task ReadManifestAsync(CancellationToken ct)
    {
        Manifest = await JsonAsync<BackupManifest>("manifest.json", ct);
        if (Manifest.Format != "scaleatlas-wiki-backup" || Manifest.Version is not (BackupFormat.Version or BackupFormat.ItemAccessVersion or BackupFormat.TemplateVersion or BackupFormat.ChangeSummaryVersion or BackupFormat.CoverVersion or BackupFormat.CoverChoiceVersion or BackupFormat.SpaceTrashVersion or BackupFormat.RevisionVersionsVersion) ||
            Manifest.ContentToken.Length != 64 || Manifest.ContentToken.Any(c => !char.IsAsciiHexDigit(c)) ||
            Manifest.CreatedAt.Kind != DateTimeKind.Utc || Manifest.Files.Count != m_files.Count - 1 || Manifest.Files.Any(x => x is null))
        {
            throw new InvalidDataException("Nepodporovaný nebo neúplný formát zálohy.");
        }
        BackupFormat.RequireMode(Manifest.Mode);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Manifest.Files)
        {
            BackupFormat.RequirePath(entry.Path);
            if (entry.Path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || !listed.Add(entry.Path) ||
                !m_files.TryGetValue(entry.Path, out var actual) || actual.Entry != entry)
            {
                throw new InvalidDataException("Kontrolní součet, velikost nebo seznam souborů zálohy nesouhlasí.");
            }
        }
    }

    private async Task ReadWikiAsync(CancellationToken ct)
    {
        Wiki = await JsonAsync<BackupWiki>("wiki.json", ct);
        RequireIds(Wiki.Spaces);
        RequireIds(Wiki.Pages);
        RequireIds(Wiki.Attachments);
        if (Wiki.Pages.Concat(Wiki.Spaces).Concat(Wiki.Attachments).Distinct().Count() != Wiki.Pages.Count + Wiki.Spaces.Count + Wiki.Attachments.Count)
        {
            throw new InvalidDataException("Identifikátory prostorů, stránek a příloh se nesmějí překrývat.");
        }
        if (Wiki.Pages.Count > 10000 || Wiki.Spaces.Count > 10000)
        {
            throw new InvalidDataException("Záloha překračuje limit stránek nebo prostorů.");
        }
    }

    private async Task ReadSpacesAsync(HashSet<string> expected, CancellationToken ct)
    {
        foreach (var id in Wiki.Spaces)
        {
            var path = $"spaces/{id:D}/space.json";
            expected.Add(path);
            var space = await JsonAsync<Space>(path, ct);
            ValidateSpace(id, space);
            Spaces.Add(space);
            await ReadCoverAsync(id, expected, ct);
            await ReadTrashAsync(id, expected, ct);
        }
    }

    /// <summary>Format 7, full archives only: the space was in the trash.</summary>
    private async Task ReadTrashAsync(Guid spaceId, HashSet<string> expected, CancellationToken ct)
    {
        var path = BackupFormat.TrashMetadataPath(spaceId);
        if (!m_files.ContainsKey(path))
        {
            return;
        }
        if (Manifest.Version < BackupFormat.SpaceTrashVersion || Manifest.Mode == "latest")
        {
            throw new InvalidDataException("Prostor v koši vyžaduje úplnou zálohu novějšího formátu.");
        }
        expected.Add(path);
        var trash = await JsonAsync<BackupSpaceTrash>(path, ct);
        if (trash.TrashBatchId == Guid.Empty || trash.TrashedAt == default)
        {
            throw new InvalidDataException("Neplatný stav koše prostoru.");
        }
        Trash.Add(spaceId, trash);
    }

    /// <summary>The image of a space is optional per space and exists only from format 5; the bytes of an upload are checked like
    /// an image attachment. A choice without a file (a member <c>source</c>) needs format 6.</summary>
    private async Task ReadCoverAsync(Guid spaceId, HashSet<string> expected, CancellationToken ct)
    {
        var path = BackupFormat.CoverMetadataPath(spaceId);
        if (!m_files.ContainsKey(path))
        {
            return;
        }
        if (Manifest.Version < BackupFormat.CoverVersion)
        {
            throw new InvalidDataException("Úvodní obrázek prostoru vyžaduje novější formát zálohy.");
        }
        expected.Add(path);
        var cover = await JsonAsync<BackupSpaceCover>(path, ct);
        if (cover.Source is not null)
        {
            // A key is only bounded here: one the running catalog does not know restores as the automatic image.
            var shaped = cover.Source switch
            {
                nameof(SpaceCoverSource.Catalog) => cover.CatalogKey is { Length: >= 1 and <= 64 },
                nameof(SpaceCoverSource.None) => cover.CatalogKey is null,
                _ => false
            };
            if (Manifest.Version < BackupFormat.CoverChoiceVersion || !shaped ||
                cover is not { File: null, MimeType: null, Size: null, Sha256: null, Tinted: null })
            {
                throw new InvalidDataException("Neplatná volba obrázku prostoru.");
            }
            Covers.Add(spaceId, cover);
            return;
        }
        if (cover.MimeType is null || cover.File is null || cover.File != BackupFormat.CoverPath(spaceId, cover.MimeType) ||
            cover.Size is null or < 1 or > SpaceCoverImage.MaximumBytes || cover.Sha256 is null || cover.Sha256.Length != 64 ||
            cover.Tinted is null || cover.CatalogKey is not null || !m_files.TryGetValue(cover.File, out var binary) ||
            binary.Entry.Size != cover.Size || binary.Entry.Sha256 != cover.Sha256)
        {
            throw new InvalidDataException("Neplatný úvodní obrázek prostoru.");
        }
        expected.Add(cover.File);
        var prefix = new byte[32];
        await using (var stream = File.OpenRead(binary.File))
        {
            var length = await stream.ReadAtLeastAsync(prefix, prefix.Length, false, ct);
            if (AttachmentFiles.TryDetectImage(prefix.AsSpan(0, length)) != cover.MimeType)
            {
                throw new InvalidDataException("Typ úvodního obrázku prostoru nesouhlasí s jeho obsahem.");
            }
        }
        Covers.Add(spaceId, cover);
    }

    private static void ValidateSpace(Guid id, Space space)
    {
        // The description is capped at what a space allows (SaveSpaceSettingsHandler): the space list gives it to the cover matcher.
        if (space.Id != id || string.IsNullOrWhiteSpace(space.Name) || space.Name.Length > 200 ||
            space.Icon is null || space.Icon.Length > 40 || space.Color is null || space.Color.Length > 20 ||
            space.Description is null || space.Description.Length > 2000)
        {
            throw new InvalidDataException("Neplatná metadata prostoru.");
        }
    }

    private async Task ReadAttachmentsAsync(HashSet<string> expected, CancellationToken ct)
    {
        foreach (var id in Wiki.Attachments)
        {
            var path = $"attachments/{id:D}/attachment.json";
            expected.Add(path);
            var item = await JsonAsync<BackupAttachment>(path, ct);
            ValidateAttachment(id, item);
            expected.Add(item.File);
            await ValidateImageAsync(item, ct);
            Attachments.Add(item);
        }
    }

    private void ValidateAttachment(Guid id, BackupAttachment item)
    {
        var asset = item.Attachment;
        ValidateAttachmentDescription(asset);
        ValidateAttachmentSizeAndHash(asset);
        if (asset.Id != id || !Wiki.Pages.Contains(asset.PageId) || item.File != BackupFormat.BinaryPath(asset))
        {
            throw new InvalidDataException("Neplatná metadata přílohy.");
        }
        RequirePeriod(asset.ActiveFrom, asset.ActiveTo);
        ValidateAttachmentPayload(item);
    }

    private static void ValidateAttachmentDescription(Attachment asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > 240 || asset.MimeType is null || asset.MimeType.Length > 100)
        {
            throw new InvalidDataException("Neplatný název nebo typ přílohy.");
        }
    }

    private static void ValidateAttachmentSizeAndHash(Attachment asset)
    {
        if (asset.Size is < 0 or > AttachmentFiles.MaximumFileBytes || asset.Sha256 is null || asset.Sha256.Length != 64)
        {
            throw new InvalidDataException("Neplatná velikost nebo kontrolní součet přílohy.");
        }
    }

    private void ValidateAttachmentPayload(BackupAttachment item)
    {
        var asset = item.Attachment;
        if (!m_files.TryGetValue(item.File, out var binary) || binary.Entry.Size != asset.Size || binary.Entry.Sha256 != asset.Sha256)
        {
            throw new InvalidDataException("Obsah přílohy nesouhlasí s metadaty.");
        }
    }

    private async Task ReadPagesAsync(HashSet<string> expected, CancellationToken ct)
    {
        var revisions = new HashSet<Guid>();
        foreach (var id in Wiki.Pages)
        {
            var path = $"pages/{id:D}/page.json";
            expected.Add(path);
            var item = await JsonAsync<BackupPage>(path, ct);
            await ValidatePageAsync(id, item, revisions, expected, ct);
            Pages.Add(item);
        }
    }

    private async Task ValidatePageAsync(Guid id, BackupPage item, HashSet<Guid> revisionIds, HashSet<string> expected, CancellationToken ct)
    {
        ValidatePageIdentity(id, item);
        ValidateRevisionHistory(id, item, revisionIds);
        foreach (var revision in item.Revisions)
            await ValidateRevisionContentAsync(id, revision, expected, ct);
        ValidateCurrentContent(id, item, expected);
        ValidatePublications(id, item);
        ValidateChangeSummaries(item);
    }

    private async Task ValidateRevisionContentAsync(Guid id, BackupRevision revision, HashSet<string> expected, CancellationToken ct)
    {
        var directory = $"pages/{id:D}/history/{revision.Version}";
        expected.Add(directory + "/content.json");
        expected.Add(directory + "/content.md");
        var changes = new List<LegacyDocumentChange>();
        var content = await ContentAsync(id, revision.Version, ct, changes);
        RepairedDocuments.AddRange(changes.Select(change => new BackupDocumentIssue(id, revision.Version, revision.Title, change.Path,
            change.Code == LegacyDocumentRepair.RepeatedMark
                ? "značka byla na textu dvakrát (například odkaz v odkazu); ponechána vnitřní, vynechána vnější: " + change.Removed
                : change.Code == LegacyDocumentRepair.SplitCard
                    ? "vizitka s několika kontakty rozdělena na vizitky po jednom kontaktu"
                    : change.Code == LegacyDocumentRepair.SummaryOverview
                        ? "souhrn nasazení převeden na přehled požadavků"
                        : change.Code == LegacyDocumentRepair.HistoryDateList
                            ? "záznam historie požadavku čtený jako vnořené číslované seznamy složen zpět na řádek s datem"
                            : change.Code == LegacyDocumentRepair.TimedList
                                ? "seznam poznámek s datem převeden na časovou odrážku"
                                : change.Code == LegacyDocumentRepair.DisclosureHeading
                                    ? "rozbalovací blok převeden na nadpis s obsahem"
                                    : change.Code == LegacyDocumentRepair.LinkAttributes
                                        ? "odkaz měl cíl, rel nebo třídu, které editor nevykresluje; vynechány"
                                        : "položka seznamu nezačínala odstavcem; doplněn prázdný odstavec")));
        using var json = JsonDocument.Parse(content, MarkdownDocumentValidator.JsonOptions);
        IReadOnlySet<Guid> referenced;
        try
        {
            referenced = AttachmentReferences.Extract(content);
        }
        catch (ApiException ex)
        {
            throw new InvalidDataException("Neplatný interní odkaz přílohy.", ex);
        }
        if (referenced.Any(x => !revision.Attachments.Contains(x)))
        {
            throw new InvalidDataException("Rich text odkazuje na přílohu mimo uloženou verzi.");
        }
        try
        {
            // Exactly the import's rules (WikiBackupImport): the JSON as every other write takes it and the Markdown the server writes
            // from it within its limit, so a preview never passes what the import then refuses after the safety backup (D-L2).
            Rules.ValidateDocument(content);
        }
        catch (ApiException ex)
        {
            var violation = HostDocumentValidator.Instance.Validate(content);
            InvalidDocuments.Add(new BackupDocumentIssue(id, revision.Version, revision.Title, violation?.Path,
                violation is null ? ex.Message : EditorDocumentErrors.Reason(violation)));
        }
    }

    private void ValidateCurrentContent(Guid id, BackupPage item, HashSet<string> expected)
    {
        var page = item.Page;
        var current = item.Revisions.Single(x => x.ActiveTo is null);
        if (page.Version != current.Version || item.Revisions.Any(x => x.Version > current.Version) ||
            (page.PublishedRevisionId is { } published && !item.Revisions.Any(x => x.Id == published)))
        {
            throw new InvalidDataException("Aktuální nebo publikovaná verze stránky chybí.");
        }
        var prefix = $"pages/{id:D}";
        expected.Add(prefix + "/content.json");
        expected.Add(prefix + "/content.md");
        if (!m_files.TryGetValue(prefix + "/content.json", out var currentFile) ||
            currentFile.Entry.Sha256 != m_files[$"{prefix}/history/{current.Version}/content.json"].Entry.Sha256)
        {
            throw new InvalidDataException("Aktuální obsah stránky nesouhlasí s historií.");
        }
    }

    private static void ValidatePublications(Guid id, BackupPage item)
    {
        var page = item.Page;
        var publicationIds = new HashSet<Guid>();
        DateTime? publicationEnd = DateTime.MinValue;
        foreach (var publication in item.Publications.OrderBy(x => x.ActiveFrom))
        {
            RequirePeriod(publication.ActiveFrom, publication.ActiveTo);
            if (publication.Id == Guid.Empty || !publicationIds.Add(publication.Id) || publication.PageId != id ||
                !item.Revisions.Any(x => x.Id == publication.RevisionId) || publicationEnd is null || publicationEnd > publication.ActiveFrom)
            {
                throw new InvalidDataException("Neplatná historie publikování.");
            }
            publicationEnd = publication.ActiveTo;
        }
        var activePublications = item.Publications.Where(x => x.ActiveTo is null).ToList();
        if (activePublications.Count > 1 ||
            (activePublications.Count == 1 && activePublications[0].RevisionId != page.PublishedRevisionId))
        {
            throw new InvalidDataException("Publikovaná verze nesouhlasí s historií publikování.");
        }
    }

    /// <summary>Change summaries (format 4) belong to publications of this page and are already in the stored form: one trimmed line of 1-500 characters.</summary>
    private void ValidateChangeSummaries(BackupPage item)
    {
        if (item.ChangeSummaries is not { } summaries)
        {
            return;
        }
        if (Manifest.Version < BackupFormat.ChangeSummaryVersion || summaries.Count == 0 ||
            summaries.Any(x => !item.Publications.Any(publication => publication.Id == x.Key) ||
                               x.Value is not { Length: > 0 and <= 500 } text || text != text.Trim() || text.Any(char.IsControl)))
        {
            throw new InvalidDataException("Neplatné popisy změn publikací.");
        }
    }

    private void ValidateRevisionHistory(Guid id, BackupPage item, HashSet<Guid> revisionIds)
    {
        DateTime? previousEnd = DateTime.MinValue;
        foreach (var revision in item.Revisions.OrderBy(x => x.ActiveFrom))
        {
            RequirePeriod(revision.ActiveFrom, revision.ActiveTo);
            if (revision.Id == Guid.Empty || !revisionIds.Add(revision.Id) || revision.Version < 1 ||
                string.IsNullOrWhiteSpace(revision.Title) || revision.Title.Length > 200 ||
                previousEnd is null || previousEnd > revision.ActiveFrom)
            {
                throw new InvalidDataException("Historie stránky obsahuje neplatné nebo překrývající se verze.");
            }
            previousEnd = revision.ActiveTo;
            if (revision.Group is { } group && (Manifest.Version < BackupFormat.RevisionVersionsVersion || group.Checkpoints < 1
                    || group.StartedAt.Kind != DateTimeKind.Utc || group.StartedAt > revision.ActiveFrom
                    || group.Label is not null && (group.Label.Length is 0 or > 500 || group.Label != group.Label.Trim() || group.Label.Any(char.IsControl))
                    || group.Changes is not null && !ValidChanges(group.Changes)))
            {
                throw new InvalidDataException("Historie stránky obsahuje neplatné údaje o verzích.");
            }
            RequireIds(revision.Attachments);
            foreach (var attachmentId in revision.Attachments)
            {
                if (!Attachments.Any(x => x.Attachment.Id == attachmentId && x.Attachment.PageId == id))
                {
                    throw new InvalidDataException("Příloha verze nepatří ke stránce.");
                }
            }
        }
    }

    private static bool ValidChanges(string json)
    {
        try
        {
            return json.Length <= 65536 && RevisionVersions.Read(json) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ValidatePageIdentity(Guid id, BackupPage item)
    {
        var page = item.Page;
        if (page.Id != id || !Wiki.Spaces.Contains(page.SpaceId) || (page.ParentId is { } parent && !Wiki.Pages.Contains(parent)) ||
            page.SortOrder <= 0 || item.Revisions.Any(x => x is null) || item.Publications.Any(x => x is null) ||
            item.Revisions.Count == 0 || item.Revisions.Count(x => x.ActiveTo is null) != 1 ||
            item.Revisions.Select(x => x.Version).Distinct().Count() != item.Revisions.Count ||
            (Manifest.Mode == "latest" && page.IsTrashed))
        {
            throw new InvalidDataException("Neplatné odkazy nebo verze stránky.");
        }
    }

    private void ValidateParents()
    {
        var pages = Pages.ToDictionary(x => x.Page.Id, x => x.Page);
        foreach (var page in pages.Values)
        {
            var seen = new HashSet<Guid> { page.Id };
            var parent = page.ParentId;
            while (parent is { } id)
            {
                if (!seen.Add(id) || pages[id].SpaceId != page.SpaceId || (!page.IsTrashed && pages[id].IsTrashed))
                {
                    throw new InvalidDataException("Strom stránek obsahuje cyklus nebo rodiče z jiného prostoru.");
                }
                parent = pages[id].ParentId;
            }
        }
    }

    private static void RequireIds(List<Guid> ids)
    {
        if (ids.Any(x => x == Guid.Empty) || ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidDataException("Záloha obsahuje duplicitní nebo prázdné identifikátory.");
        }
    }

    private async Task ValidateImageAsync(BackupAttachment item, CancellationToken ct)
    {
        if (!item.Attachment.IsImage)
            return;
        if (item.Attachment.Size > AttachmentFiles.MaximumImageBytes)
            throw new InvalidDataException("Obrázek překračuje povolenou velikost 10 MiB.");
        await using var stream = File.OpenRead(FileFor(item.File));
        var prefix = new byte[32];
        var length = await stream.ReadAtLeastAsync(prefix, Math.Min(prefix.Length, (int)stream.Length), false, ct);
        try
        {
            if (AttachmentFiles.DetectImage(prefix.AsSpan(0, length)) != item.Attachment.MimeType)
                throw new InvalidDataException("Typ obrázku nesouhlasí s binárním obsahem.");
        }
        catch (ApiException ex)
        {
            throw new InvalidDataException("Záloha obsahuje nepodporovaný obrázek.", ex);
        }
    }

    private static void RequirePeriod(DateTime from, DateTime? to)
    {
        if (from.Kind != DateTimeKind.Utc || from.Year < 1900 || (to is { } end && (end.Kind != DateTimeKind.Utc || end <= from)))
        {
            throw new InvalidDataException("Neplatný časový interval v záloze.");
        }
    }

    public void Dispose() => Directory.Delete(m_directory, true);
}
