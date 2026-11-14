using System.Security.Cryptography;
using NHibernate;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.SpaceCovers.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Persistence;
using Wolverine.Runtime;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Backups.Archive;

/// <summary>A revision of the archive the server would refuse on any other write: the page (its archived id), the version, its title,
/// and the schema path (null when the envelope, a size limit or a password reference fails) with the reason.</summary>
public sealed record BackupDocumentIssue(Guid PageId, int Version, string Title, string? Path, string Reason);

/// <param name="InvalidDocuments">The first <see cref="WikiBackupImport.MaximumReportedIssues"/> of <paramref name="Invalid"/> refused revisions.</param>
/// <param name="Repaired">Repairs of Atlas 2.x shapes the schema refuses (<see cref="Atlas.Markdown.Sync.LegacyDocumentRepair"/>); the import stores the
/// repaired revisions. <paramref name="RepairedDocuments"/> lists the first <see cref="WikiBackupImport.MaximumReportedIssues"/>.</param>
public sealed record BackupImportPreview(string Suffix, int Pages, int Attachments, string Message, int Invalid = 0,
    IReadOnlyList<BackupDocumentIssue>? InvalidDocuments = null, int Repaired = 0, IReadOnlyList<BackupDocumentIssue>? RepairedDocuments = null);
public sealed record BackupImportResult(int Pages, int Attachments, string Message);

/// <summary>Restores validated saved content. The caller authorizes the administrator and creates the safety archive.</summary>
/// <remarks>
/// Every revision is read with the Atlas 2.x shapes the editor repairs on open already repaired (<see cref="Atlas.Markdown.Sync.LegacyDocumentRepair"/>,
/// listed in the preview) and validated as any other write is (<see cref="Rules.ValidateDocument(string?)"/>) and stored with the Markdown the
/// server writes from its JSON; the archive's own content.md and SourceMarkdown are never stored. An archive with revisions the
/// editor would refuse is refused as a whole, with the list, before anything is written; there is no way to take them as they are.
/// Recovery: fix the listed revisions in the archive, or restore it with the Atlas version that wrote it and upgrade through the
/// normal release path.
/// </remarks>
public sealed class WikiBackupImport(AtlasDatabase database, AttachmentFiles attachments, IServiceScopeFactory scopes)
{
    public const int MaximumReportedIssues = 200;

    public async Task<BackupImportPreview> PreviewAsync(Stream zip, string mode, CancellationToken ct)
    {
        BackupFormat.RequireMode(mode, true);
        using var archive = await BackupArchiveReader.ReadAsync(zip, ct);
        return Preview(archive, mode);
    }

    private static BackupImportPreview Preview(BackupArchiveReader archive, string mode)
    {
        var suffix = mode == "add" ? BackupFormat.Suffix(archive.Manifest) : "";
        return new BackupImportPreview(suffix, archive.Pages.Count, archive.Attachments.Count, Describe(mode, suffix),
            archive.InvalidDocuments.Count, archive.InvalidDocuments.Take(MaximumReportedIssues).ToList(),
            archive.RepairedDocuments.Count, archive.RepairedDocuments.Take(MaximumReportedIssues).ToList());
    }

    public async Task<BackupImportResult> ImportAsync(Stream zip, string mode, Guid actorId, CancellationToken ct)
    {
        BackupFormat.RequireMode(mode, true);
        using var archive = await BackupArchiveReader.ReadAsync(zip, ct);
        RequireValidDocuments(Preview(archive, mode));
        using var scope = scopes.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<WikiBackupImport>>();
        using var session = database.OpenSession();
        using var transaction = session.BeginTransaction();
        // Outside a handler: the outbox's envelopes are written in the import's own transaction and go to their queue after its commit.
        var messages = scope.ServiceProvider.GetRequiredService<MessageContext>();
        await NHibernateEnvelopeTransaction.EnlistAsync(session, transaction, messages);
        var outbox = scope.ServiceProvider.GetRequiredService<Outbox>();
        outbox.Begin(messages);
        var createdFiles = new List<Guid>();
        var releasedFiles = new List<Guid>();
        var commitAttempted = false;
        try
        {
            var suffix = await ImportContentAsync(session, outbox, archive, mode, actorId, createdFiles, releasedFiles, ct);
            await outbox.WriteAsync();
            await session.FlushAsync(ct);
            commitAttempted = true;
            await transaction.CommitAsync(ct);
            await messages.FlushOutgoingMessagesAsync();
            RemoveReleasedFiles(releasedFiles, logger);
            return new BackupImportResult(archive.Pages.Count, archive.Attachments.Count, Describe(mode, suffix));
        }
        catch
        {
            if (!commitAttempted)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                RemoveUncommittedFiles(createdFiles, logger);
            }
            else
            {
                // A lost commit response is ambiguous. Retaining newly written immutable blobs avoids dangling
                // references if PostgreSQL committed; these unreferenced files can be reconciled later if it did not.
                logger.LogWarning("Backup import commit result is uncertain; retaining {Count} staged attachment files", createdFiles.Count);
            }
            throw;
        }
    }

    /// <summary>Refuses an archive with invalid revisions, with their list; the endpoint
    /// runs it before the safety archive and the import, so a refusal changes nothing.</summary>
    public static void RequireValidDocuments(BackupImportPreview preview)
    {
        if (preview.Invalid > 0)
        {
            throw new ApiException(400, $"Záloha obsahuje {preview.Invalid} verzí stránek, které editor Atlasu neotevře. "
                + "Nic nebylo změněno. Opravte uvedené verze v archivu, nebo zálohu obnovte verzí Atlasu, která ji vytvořila, a aktualizujte běžnou cestou.",
                "invalid_documents", new Dictionary<string, object?> { ["documents"] = preview.InvalidDocuments });
        }
    }

    private void RemoveUncommittedFiles(List<Guid> createdFiles, ILogger<WikiBackupImport> logger)
    {
        foreach (var id in createdFiles)
        {
            try
            {
                attachments.Remove(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove uncommitted backup attachment {AttachmentId}", id);
            }
        }
    }

    /// <summary>Files of card images the committed restore replaced; a failure only leaves an unreferenced file.</summary>
    private void RemoveReleasedFiles(List<Guid> releasedFiles, ILogger<WikiBackupImport> logger)
    {
        foreach (var id in releasedFiles)
        {
            try
            {
                attachments.Remove(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove the replaced space cover file {FileId}", id);
            }
        }
    }

    private async Task<string> ImportContentAsync(ISession session, Outbox outbox, BackupArchiveReader archive, string mode,
        Guid actorId, List<Guid> createdFiles, List<Guid> releasedFiles, CancellationToken ct)
    {
        var locks = new AtlasLocks(session);
        await locks.AcquireContentExclusiveAsync(ct);
        await locks.SetActorAsync(actorId, ct);
        // Recheck the actor inside the same write transaction: archives never supply accounts or privileges.
        await locks.LockAccountSharedAsync(actorId, ct);
        var actor = await session.GetAsync<UserAccount>(actorId, ct);
        if (actor is null || !actor.IsActive || !await EffectiveWikiAdmin.IsAdminAsync(session, actor, ct))
        {
            throw new ApiException(403, "Obnovu může provést pouze správce wiki.");
        }
        var add = mode == "add";
        var suffix = add ? BackupFormat.Suffix(archive.Manifest) : "";
        if (!add)
        {
            await RequirePreservedChildrenAsync(session, archive, ct);
        }
        var ids = archive.Wiki.Pages.Concat(archive.Wiki.Spaces).Distinct().ToDictionary(x => x, x => add ? Guid.NewGuid() : x);
        var trashBatches = archive.Pages.Where(x => x.Page.TrashBatchId is not null).Select(x => x.Page.TrashBatchId!.Value)
            .Concat(archive.Trash.Values.Select(x => x.TrashBatchId)).Distinct().ToDictionary(x => x, x => add ? Guid.NewGuid() : x);
        var pages = new Dictionary<Guid, (Page Page, bool Existing)>();
        await ImportSpacesAsync(session, archive, ids, trashBatches, actorId, suffix, ct);
        await ImportCoversAsync(session, archive, ids, actorId, createdFiles, releasedFiles, ct);
        await ImportPageAnchorsAsync(session, archive, ids, pages, actorId, ct);
        var spacesBefore = pages.Values.Where(x => x.Existing).ToDictionary(x => x.Page.Id, x => x.Page.SpaceId);
        var trashedBefore = pages.Values.Where(x => x.Existing && x.Page.IsTrashed).Select(x => x.Page.Id).ToHashSet();
        RestorePlacement(archive, pages, ids, trashBatches, actorId);
        await session.FlushAsync(ct);
        // Archives carry no item grants (like space grants). A new page gets the space defaults (OwnAccess=false, no grants),
        // exact for format 1 archives, which come from instances without own access; an existing page keeps its mode and
        // grants, unless the restore moves it to another space, where D7 applies as for a cross-space move.
        var moved = pages.Values.Where(x => x.Existing && spacesBefore[x.Page.Id] != x.Page.SpaceId).Select(x => x.Page).ToArray();
        // An existing page the restore moves to another space or into the trash ends its share links, as a move or a trash does.
        await ShareRevocation.PagesAsync(session, [.. moved.Select(x => x.Id)
            .Union(pages.Values.Where(x => x.Existing && x.Page.IsTrashed && !trashedBefore.Contains(x.Page.Id)).Select(x => x.Page.Id))], actorId, ct);
        if (moved.Length > 0)
        {
            await PageItemAccess.ResetAsync(session, moved, actorId, ct);
        }
        // Format 2 (WP3 S5): a page that had own access is restored with own access and no grants, so only the space Admin and
        // WikiAdmin see it until access is granted again. A restore never widens access: own access is only ever turned on.
        foreach (var source in archive.Pages.Where(x => x.OwnAccess))
        {
            pages[source.Page.Id].Page.OwnAccess = true;
            await PageAccessModeHistory.RecordAsync(session, pages[source.Page.Id].Page, actorId, ct);
        }
        // Existing template status is never silently lowered by an older archive.
        foreach (var source in archive.Pages.Where(x => x.IsTemplate))
        {
            pages[source.Page.Id].Page.IsTemplate = true;
        }
        await session.FlushAsync(ct);
        await ImportAttachmentsAsync(session, archive, ids, actorId, add, createdFiles, ct);
        // The archive's revisions and publications are no news of the restorer's: each page is announced as a rebuild, which records its
        // mentions and tells nobody (GAP2-4).
        foreach (var source in archive.Pages)
        {
            var target = pages[source.Page.Id];
            await ImportRevisionsAsync(session, archive, source, target.Page, target.Existing, ids, actorId, suffix, ct);
            outbox.ContentRebuilt(target.Page.Id, Pages.PagesAtlasModule.ContentType);
        }
        await Search.SearchDocuments.RefreshAsync(session, archive.Pages.Select(source => pages[source.Page.Id].Page.Id).ToArray(), ct);
        return suffix;
    }

    private static async Task ImportSpacesAsync(ISession session, BackupArchiveReader archive,
        Dictionary<Guid, Guid> ids, Dictionary<Guid, Guid> trashBatches, Guid actorId, string suffix, CancellationToken ct)
    {
        foreach (var source in archive.Spaces)
        {
            var id = ids[source.Id];
            var space = await session.GetAsync<Space>(id, LockMode.Upgrade, ct);
            var created = space is null;
            if (space is null)
            {
                space = new Space { Id = id };
                await session.SaveAsync(space, ct);
            }
            space.Name = BackupFormat.WithSuffix(source.Name, suffix);
            space.Icon = source.Icon;
            space.Color = source.Color;
            space.Description = source.Description;
            // Format 7: a space the restore creates returns to the trash with its archived pages (same batch). An existing live space
            // stays live: it may hold pages the archive does not know, and a trashed space must hold no live page. An existing
            // trashed space the archive has live comes back, its other pages stay in its page trash.
            var trash = archive.Trash.GetValueOrDefault(source.Id);
            if (trash is not null && created)
            {
                (space.TrashedAt, space.TrashedById, space.TrashBatchId) = (trash.TrashedAt, actorId, trashBatches[trash.TrashBatchId]);
            }
            else if (trash is null)
            {
                (space.TrashedAt, space.TrashedById, space.TrashBatchId) = (null, null, null);
            }
        }
        await session.FlushAsync(ct);
    }

    /// <summary>Format 5: an archived uploaded image becomes the current one of its space, as the restorer's change; an identical
    /// current image stays. Format 6: so does an archived choice (a drawing, no image); a drawing the running catalog does not
    /// know restores as the automatic image. A space the archive has no image for keeps its own, as an existing template keeps
    /// its flag: an older archive cannot tell "no image" from "before images".</summary>
    private async Task ImportCoversAsync(ISession session, BackupArchiveReader archive, Dictionary<Guid, Guid> ids, Guid actorId,
        List<Guid> createdFiles, List<Guid> releasedFiles, CancellationToken ct)
    {
        foreach (var (sourceId, source) in archive.Covers)
        {
            var spaceId = ids[sourceId];
            if (source.Source is not null)
            {
                var choice = source.Source == nameof(SpaceCoverSource.None) ? SpaceCoverSource.None
                    : CoverCatalog.Knows(source.CatalogKey) ? SpaceCoverSource.Catalog : SpaceCoverSource.Auto;
                var key = choice == SpaceCoverSource.Catalog ? source.CatalogKey : null;
                if (await SpaceCoverHistory.ChooseAsync(session, spaceId, choice, key, actorId, ct) is { } replaced)
                {
                    releasedFiles.Add(replaced);
                }
                continue;
            }
            var current = await SpaceCoverHistory.CurrentAsync(session, spaceId, ct);
            if (current is { FileId: { } existing } && current.Sha256 == source.Sha256 && current.Tinted == source.Tinted && File.Exists(attachments.PathFor(existing)))
            {
                continue;
            }
            var fileId = Guid.NewGuid();
            await using (var input = File.OpenRead(archive.FileFor(source.File!)))
            {
                var written = await attachments.WriteAsync(fileId, input, true, ct);
                createdFiles.Add(fileId);
                if (written.Hash != source.Sha256 || written.Size != source.Size || written.MimeType != source.MimeType)
                {
                    throw new InvalidDataException("Úvodní obrázek prostoru se při obnovování změnil.");
                }
            }
            await SpaceCoverHistory.AdvanceAsync(session, current, new SpaceCover
            {
                Id = Guid.NewGuid(), SpaceId = spaceId, Source = SpaceCoverSource.Upload, FileId = fileId, MimeType = source.MimeType,
                Size = source.Size, Sha256 = source.Sha256, Tinted = source.Tinted!.Value, ChangedBy = actorId
            }, ct);
            if (current?.FileId is { } released)
            {
                releasedFiles.Add(released);
            }
        }
    }

    private static async Task ImportPageAnchorsAsync(ISession session, BackupArchiveReader archive, Dictionary<Guid, Guid> ids,
        Dictionary<Guid, (Page Page, bool Existing)> pages, Guid actorId, CancellationToken ct)
    {
        // Insert all page anchors before parent links, revision foreign keys or attachments.
        foreach (var source in archive.Pages)
        {
            var id = ids[source.Page.Id];
            // The check of preserved children may already hold this page: it is taken as it is under the lock.
            var page = await session.GetForUpdateAsync<Page>(id, ct);
            var existing = page is not null;
            if (page is null)
            {
                page = new Page { Id = id, SpaceId = ids[source.Page.SpaceId], OwnerId = actorId };
                await session.SaveAsync(page, ct);
                await PageAccessModeHistory.RecordAsync(session, page, actorId, ct);
            }
            pages.Add(source.Page.Id, (page, existing));
        }
        await session.FlushAsync(ct);
    }

    private static void RestorePlacement(BackupArchiveReader archive, Dictionary<Guid, (Page Page, bool Existing)> pages,
        Dictionary<Guid, Guid> ids, Dictionary<Guid, Guid> trashBatches, Guid actorId)
    {
        foreach (var source in archive.Pages)
        {
            var page = pages[source.Page.Id].Page;
            page.SpaceId = ids[source.Page.SpaceId];
            page.ParentId = source.Page.ParentId is { } parent ? ids[parent] : null;
            page.SortOrder = source.Page.SortOrder;
            page.IsTrashed = source.Page.IsTrashed;
            page.TrashedAt = source.Page.IsTrashed ? source.Page.TrashedAt ?? DateTime.UtcNow : null;
            page.TrashedById = source.Page.IsTrashed ? actorId : null;
            page.TrashBatchId = source.Page.IsTrashed ? (source.Page.TrashBatchId is { } batch ? trashBatches[batch] : Guid.NewGuid()) : null;
        }
    }

    private static async Task RequirePreservedChildrenAsync(ISession session, BackupArchiveReader archive, CancellationToken ct)
    {
        var restored = archive.Pages.ToDictionary(x => x.Page.Id, x => x.Page);
        if (restored.Count == 0)
        {
            return;
        }
        var children = await session.QueryOver<Page>().Where(PageSubtree.ChildrenOf(restored.Keys)).ListAsync(ct);
        if (children.Any(child => !restored.ContainsKey(child.Id) && child.ParentId is { } parent &&
            (child.SpaceId != restored[parent].SpaceId || (!child.IsTrashed && restored[parent].IsTrashed))))
        {
            throw new ApiException(409, "Obnova by oddělila novější podstránky od jejich prostoru nebo je skryla pod košem. Nejprve je přesuňte.");
        }
    }

    private async Task ImportAttachmentsAsync(ISession session, BackupArchiveReader archive, Dictionary<Guid, Guid> ids,
        Guid actor, bool add, List<Guid> createdFiles, CancellationToken ct)
    {
        foreach (var source in archive.Attachments)
        {
            var asset = source.Attachment;
            var id = add ? Guid.NewGuid() : asset.Id;
            var existing = await session.GetAsync<Attachment>(id, ct);
            if (existing is not null && !SameAttachment(existing, asset, ids[asset.PageId]))
            {
                id = Guid.NewGuid();
                existing = null;
            }
            if (File.Exists(attachments.PathFor(id)))
            {
                await using var previous = attachments.Open(id);
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(previous, ct));
                if (previous.Length != asset.Size || hash != asset.Sha256)
                {
                    id = Guid.NewGuid();
                    existing = null;
                }
            }
            ids[asset.Id] = id;
            if (existing is not null)
            {
                if (!File.Exists(attachments.PathFor(id)))
                {
                    // Recreate missing immutable bytes from the validated archive without changing existing rows.
                    await WriteAttachmentAsync(archive, source, id, createdFiles, ct);
                }
                continue;
            }
            if (!File.Exists(attachments.PathFor(id)))
            {
                await WriteAttachmentAsync(archive, source, id, createdFiles, ct);
            }
            await session.SaveAsync(new Attachment
            {
                Id = id, PageId = ids[asset.PageId], UploadedBy = actor, Name = asset.Name,
                MimeType = asset.MimeType, Size = asset.Size, Sha256 = asset.Sha256, IsImage = asset.IsImage,
                ActiveFrom = asset.ActiveFrom, ActiveTo = asset.ActiveTo
            }, ct);
        }
        await session.FlushAsync(ct);
    }

    private async Task WriteAttachmentAsync(BackupArchiveReader archive, BackupAttachment source, Guid id, List<Guid> createdFiles, CancellationToken ct)
    {
        await using var input = File.OpenRead(archive.FileFor(source.File));
        var written = await attachments.WriteAsync(id, input, source.Attachment.IsImage, ct);
        createdFiles.Add(id);
        if (written.Hash != source.Attachment.Sha256 || written.Size != source.Attachment.Size)
        {
            throw new InvalidDataException("Příloha se při obnovování změnila.");
        }
    }

    private static bool SameAttachment(Attachment existing, Attachment source, Guid pageId) =>
        existing.PageId == pageId && existing.Sha256 == source.Sha256 && existing.Size == source.Size &&
        existing.Name == source.Name && existing.MimeType == source.MimeType && existing.IsImage == source.IsImage &&
        existing.ActiveFrom == source.ActiveFrom && existing.ActiveTo == source.ActiveTo;

    private static async Task ImportRevisionsAsync(ISession session, BackupArchiveReader archive, BackupPage source, Page page,
        bool existing, Dictionary<Guid, Guid> ids, Guid actor, string suffix, CancellationToken ct)
    {
        var originals = source.Revisions.OrderBy(x => x.Version).ToList();
        var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        var lastVersion = existing ? await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id).SelectList(x => x.SelectMax(r => r.Version)).SingleOrDefaultAsync<int>(ct) : 0;
        var boundary = current is null ? DateTime.UtcNow : Temporal.NextBoundary(current.ActiveFrom, DateTime.UtcNow);
        if (current is not null)
        {
            current.ActiveTo = boundary;
            await session.FlushAsync(ct);
        }
        var revisionMap = new Dictionary<Guid, Guid>();
        var groupStarts = new Dictionary<Guid, DateTime>();
        PageRevision restoredCurrent = null!;
        Guid? previousId = null;
        for (var index = 0; index < originals.Count; index++)
        {
            var revision = originals[index];
            var id = suffix.Length > 0 || existing || await session.GetAsync<PageRevision>(revision.Id, ct) is not null ? Guid.NewGuid() : revision.Id;
            revisionMap.Add(revision.Id, id);
            var from = existing ? boundary.AddTicks(index * 10L) : revision.ActiveFrom;
            var to = existing ? (revision.ActiveTo is null ? (DateTime?)null : boundary.AddTicks((index + 1) * 10L)) : revision.ActiveTo;
            var content = BackupFormat.RemapJson(await archive.ContentAsync(source.Page.Id, revision.Version, ct), ids);
            var saved = new PageRevision
            {
                Id = id, PageId = page.Id, Version = existing ? ++lastVersion : revision.Version,
                Title = BackupFormat.WithSuffix(revision.Title, suffix),
                ContentJson = content,
                Markdown = Rules.ValidateDocument(content).Markdown,
                AuthorId = actor, ActiveFrom = from, ActiveTo = to,
                // The restorer becomes the author and the restore the channel: backups carry neither, tokens are not backed up.
                Channel = ContentChannels.Import
            };
            // A version whose first revision a latest archive left out starts again here; one without version columns is its own.
            var group = revision.Group;
            saved.GroupId = group is not null && revisionMap.TryGetValue(group.GroupId, out var groupId) ? groupId : id;
            saved.GroupStartedAt = saved.GroupId == id ? (existing || group is null ? from : group.StartedAt) : groupStarts[saved.GroupId];
            groupStarts.TryAdd(saved.GroupId, saved.GroupStartedAt);
            saved.CheckpointCount = group?.Checkpoints ?? 1;
            saved.IsExplicit = group?.IsExplicit ?? false;
            saved.Label = group?.Label;
            saved.BaseRevisionId = group?.BaseRevisionId is { } basis && revisionMap.TryGetValue(basis, out var mapped) ? mapped : previousId;
            saved.ChangeSummary = group?.Changes;
            previousId = id;
            await session.SaveAsync(saved, ct);
            await session.FlushAsync(ct);
            // Historical bindings are preserved, including attachments closed since the revision was saved.
            foreach (var attachment in revision.Attachments)
            {
                await session.SaveAsync(new PageRevisionAttachment { PageId = page.Id, RevisionId = id, AttachmentId = ids[attachment] }, ct);
            }
            if (revision.ActiveTo is null)
            {
                restoredCurrent = saved;
            }
        }
        page.Version = restoredCurrent.Version;
        page.PublishedRevisionId = source.Page.PublishedRevisionId is { } published ? revisionMap[published] : null;
        await ImportPublicationsAsync(session, source, page, existing, revisionMap, actor, boundary, ct);
        await new CollaborationCheckpoints(session).ResetAsync(page, restoredCurrent, ct);
    }

    private static async Task ImportPublicationsAsync(ISession session, BackupPage source, Page page, bool existing,
        Dictionary<Guid, Guid> revisions, Guid actor, DateTime boundary, CancellationToken ct)
    {
        var current = await session.QueryOver<PagePublication>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        if (current is not null)
        {
            boundary = Temporal.NextBoundary(current.ActiveFrom, boundary);
            current.ActiveTo = boundary;
            await session.FlushAsync(ct);
        }
        var publications = source.Publications.OrderBy(x => x.ActiveFrom).ToList();
        for (var index = 0; index < publications.Count; index++)
        {
            var publication = publications[index];
            await session.SaveAsync(new PagePublication
            {
                Id = Guid.NewGuid(), PageId = page.Id, RevisionId = revisions[publication.RevisionId], PublisherId = actor,
                ChangeSummary = source.ChangeSummaries?.GetValueOrDefault(publication.Id),
                ActiveFrom = existing ? boundary.AddTicks(index * 10L) : publication.ActiveFrom,
                ActiveTo = existing ? (publication.ActiveTo is null ? null : boundary.AddTicks((index + 1) * 10L)) : publication.ActiveTo,
                Channel = ContentChannels.Import
            }, ct);
        }
        if (page.PublishedRevisionId is { } published && !publications.Any(x => x.ActiveTo is null && revisions[x.RevisionId] == published))
        {
            await session.SaveAsync(new PagePublication { Id = Guid.NewGuid(), PageId = page.Id, RevisionId = published, PublisherId = actor,
                ActiveFrom = boundary.AddTicks(publications.Count * 10L), Channel = ContentChannels.Import }, ct);
        }
    }

    private static string Describe(string mode, string suffix) => mode == "add"
        ? $"Obsah bude přidán pod novými UUID adresami. Názvy prostorů a stránek dostanou příponu {suffix}."
        : "Obnoví obsah a umístění původních stránek. Existující historie zůstane zachována; zálohované verze se přidají do historie. Účty a oprávnění se neimportují.";
}
