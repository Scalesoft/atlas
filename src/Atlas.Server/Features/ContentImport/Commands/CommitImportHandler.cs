using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NHibernate;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.ContentImport.Contracts;
using Atlas.Server.Features.ContentImport.Domain;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.PageTags.Domain;
using Atlas.Server.Features.Search;
using Atlas.Server.Platform.Events;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ContentImport.Commands;

public sealed class CommitImportHandler
{
    private const long ImportLockKey = 731145019;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly ContentImportFiles m_files;
    private readonly AttachmentFiles m_attachments;
    private readonly CollaborationCheckpoints m_checkpoints;
    private readonly PageTagStore m_tags;
    private readonly ILogger<CommitImportHandler> m_logger;
    private readonly Outbox m_outbox;

    public CommitImportHandler(ISession session, IContentAccess access, IAtlasLocks locks, ContentImportFiles files,
        AttachmentFiles attachments, CollaborationCheckpoints checkpoints, PageTagStore tags, ILogger<CommitImportHandler> logger, Outbox outbox)
    {
        m_outbox = outbox;
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_files = files;
        m_attachments = attachments;
        m_checkpoints = checkpoints;
        m_tags = tags;
        m_logger = logger;
    }

    public async Task<WikiImportReport> Handle(CommitImport command, CancellationToken ct)
    {
        var request = command.Request;
        if (!request.AcknowledgeQuarantine || !request.AcknowledgeWarnings)
        {
            throw new ApiException(400, "Potvrďte kontrolu reportu a import do neveřejného prostoru.");
        }
        var createdFiles = new List<Guid>();
        try
        {
            await m_locks.AcquireContentSharedAsync(ct);
            await m_locks.AcquireAdvisoryAsync(ImportLockKey, ct);
            var batch = await m_session.GetAsync<WikiImportBatch>(command.Id, LockMode.Upgrade, ct)
                ?? throw new ApiException(404, "Import nebyl nalezen.");
            var previousReport = JsonSerializer.Deserialize<WikiImportReport>(batch.ReportJson, Json)!;
            var destination = await m_session.QueryOver<WikiImportDestination>().Where(x => x.SourceId == batch.SourceId && x.ActiveTo == null).SingleOrDefaultAsync(ct);
            var actor = await ImportGuards.RequireAdministratorAsync(m_session, m_access, m_locks, destination?.SpaceId, ct);
            if (!FixedEquals(previousReport.ReviewToken, request.ReviewToken))
            {
                throw new ApiException(409, "Potvrzení neodpovídá tomuto náhledu.");
            }
            if (batch.CompletedAt is not null)
            {
                return previousReport;
            }
            if (batch.ExpiresAt < DateTime.UtcNow)
            {
                throw new ApiException(410, "Náhled vypršel. Nahrajte ZIP znovu.");
            }
            if (!previousReport.CanCommit)
            {
                throw new ApiException(409, "Import obsahuje konflikty. Připravte nový náhled.");
            }
            await ImportGuards.RequireQuarantineAsync(m_session, destination?.SpaceId, ct);
            await using var stream = m_files.Open(command.Id);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (hash != batch.ArchiveSha256)
            {
                throw new ApiException(409, "Původní archiv se změnil. Import byl zastaven.");
            }
            stream.Position = 0;
            var options = JsonSerializer.Deserialize<WikiImportOptions>(batch.OptionsJson, Json)!;
            var archive = await WikiJsArchive.ReadAsync(stream, options, ct);
            var (plans, planned) = await ImportPlanner.PlanAsync(m_session, archive, batch.SourceId, options, previousReport, ct);
            if (plans.Any(p => p.Item.Action == "conflict"))
            {
                throw new ApiException(409, "Cílový obsah se od náhledu změnil. Vytvořte nový náhled.");
            }
            var spaceId = destination?.SpaceId ?? Guid.NewGuid();
            if (destination is null)
            {
                await m_session.SaveAsync(new Space { Id = spaceId, Name = options.SpaceName, Icon = "archive", Color = "blue" }, ct);
                await m_session.FlushAsync(ct);
                await m_session.SaveAsync(new WikiImportDestination { Id = Guid.NewGuid(), SourceId = batch.SourceId, SpaceId = spaceId, ActiveFrom = DateTime.UtcNow }, ct);
            }
            var pageIds = plans.ToDictionary(p => ImportPlanner.RemoveExtension(p.Source.Path), p => p.Item.PageId!.Value, StringComparer.OrdinalIgnoreCase);
            // Resolve the batch's tags once, in sorted order as PageTagStore requires to avoid catalog deadlocks.
            var tagIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var (name, normalized) in TagNames.Prepare(plans.Where(p => p.Item.Action == "create").SelectMany(p => p.Source.Tags)))
            {
                tagIds[normalized] = await m_tags.ResolveAsync(name, normalized, actor, ct);
            }
            foreach (var plan in plans.OrderBy(p => p.Source.Path.Count(c => c == '/')).ThenBy(p => p.Source.Path, StringComparer.Ordinal))
            {
                if (plan.Item.Action == "skip")
                {
                    continue;
                }
                var page = plan.Item.Action == "create" ? null : await m_session.GetAsync<Page>(plan.Item.PageId!.Value, LockMode.Upgrade, ct);
                WikiImportPage map;
                if (page is null)
                {
                    var parentPath = ImportPlanner.ParentPath(ImportPlanner.RemoveExtension(plan.Source.Path));
                    Guid? parentId = null;
                    while (parentPath.Length > 0)
                    {
                        if (pageIds.TryGetValue(parentPath, out var found))
                        {
                            parentId = found;
                            break;
                        }
                        parentPath = ImportPlanner.ParentPath(parentPath);
                    }
                    page = new Page { Id = plan.Item.PageId!.Value, SpaceId = spaceId, ParentId = parentId, OwnerId = actor.Id };
                    await m_session.SaveAsync(page, ct);
                    await PageAccessModeHistory.RecordAsync(m_session, page, actor.Id, ct);
                    map = new WikiImportPage { Id = Guid.NewGuid(), SourceId = batch.SourceId, SourcePath = plan.Source.Path, PageId = page.Id };
                    await m_session.SaveAsync(map, ct);
                    await TagAsync(page, plan.Source.Tags.Select(name => tagIds[name.ToLowerInvariant()]), actor, ct);
                }
                else
                {
                    await m_session.RefreshAsync(page, LockMode.Upgrade, ct);
                    var live = await m_session.GetAsync<PageCollaboration>(page.Id, LockMode.Upgrade, ct);
                    if (live is not null && live.Sequence > 0)
                    {
                        throw new ApiException(409, "Stránka má neuložené společné změny. Import je nepřepíše.");
                    }
                    if (page.IsTrashed || page.SpaceId != spaceId || page.Version != plan.Item.ExpectedVersion || page.PublishedRevisionId is not null)
                    {
                        throw new ApiException(409, "Importovaná stránka byla změněna, publikována nebo odstraněna. Import nic nepřepsal.");
                    }
                    map = await m_session.QueryOver<WikiImportPage>().Where(x => x.SourceId == batch.SourceId && x.PageId == page.Id).SingleOrDefaultAsync(ct);
                }
                foreach (var (assetId, asset) in plan.Assets)
                {
                    if (await m_session.GetAsync<Attachment>(assetId, ct) is not null)
                    {
                        continue;
                    }
                    var image = ImportPlanner.IsSupportedImage(asset);
                    // File IDs include page, source path and hash, so repeated imports safely reuse identical assets.
                    (long Size, string Hash, string MimeType) data;
                    if (File.Exists(m_attachments.PathFor(assetId)))
                    {
                        // Adopt an exact orphan left by an ambiguous commit; never delete unverified bytes.
                        await using var orphan = m_attachments.Open(assetId);
                        var orphanHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(orphan, ct));
                        if (orphan.Length != asset.Bytes.LongLength || orphanHash != asset.Sha256)
                        {
                            throw new ApiException(409, "Soubor přílohy v úložišti nesouhlasí se zdrojem. Import vyžaduje kontrolu správcem.");
                        }
                        data = (orphan.Length, orphanHash, image ? AttachmentFiles.DetectImage(asset.Bytes) : "application/octet-stream");
                    }
                    else
                    {
                        data = await m_attachments.WriteAsync(assetId, new MemoryStream(asset.Bytes, false), image, ct);
                        createdFiles.Add(assetId);
                    }
                    await m_session.SaveAsync(new Attachment
                    {
                        Id = assetId,
                        PageId = page.Id,
                        UploadedBy = actor.Id,
                        Name = AttachmentFiles.SafeName(asset.Path),
                        MimeType = data.MimeType,
                        Size = data.Size,
                        Sha256 = data.Hash,
                        IsImage = image,
                        ActiveFrom = DateTime.UtcNow
                    }, ct);
                }
                var previous = await m_session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync(ct);
                var boundary = previous is null ? DateTime.UtcNow : Temporal.NextBoundary(previous.ActiveFrom, DateTime.UtcNow);
                if (previous is not null)
                {
                    previous.ActiveTo = boundary;
                    await m_session.FlushAsync(ct);
                    page.Version++;
                }
                var revision = new PageRevision
                {
                    Id = Guid.NewGuid(),
                    PageId = page.Id,
                    Version = page.Version,
                    Title = plan.Source.Title,
                    ContentJson = plan.Document.ContentJson,
                    Markdown = plan.Document.Markdown,
                    AuthorId = actor.Id,
                    ActiveFrom = boundary,
                    // The import is its own channel whoever starts it, and never a token's change.
                    Channel = ContentChannels.Import
                };
                await m_session.SaveAsync(revision, ct);
                m_outbox.PageRevised(page.Id, revision.Id);
                await SearchDocuments.RefreshWorkingAsync(m_session, [page.Id], ct);
                await AttachmentReferences.BindRevisionAsync(m_session, revision, ct);
                await m_checkpoints.ResetAsync(page, revision, ct);
                var checkpoint = await m_session.QueryOver<WikiImportCheckpoint>().Where(x => x.ImportPageId == map.Id && x.ActiveTo == null).SingleOrDefaultAsync(ct);
                var checkpointTime = checkpoint is null ? boundary : Temporal.NextBoundary(checkpoint.ActiveFrom, boundary);
                if (checkpoint is not null)
                {
                    checkpoint.ActiveTo = checkpointTime;
                    await m_session.FlushAsync(ct);
                }
                await m_session.SaveAsync(new WikiImportCheckpoint
                {
                    Id = Guid.NewGuid(),
                    ImportPageId = map.Id,
                    RevisionId = revision.Id,
                    SourceSha256 = plan.Item.Sha256,
                    ActiveFrom = checkpointTime
                }, ct);
            }
            var report = ImportReportBuilder.Build(command.Id, previousReport.ReviewToken, batch.ExpiresAt, spaceId, options.SpaceName, planned, plans)
                with { CompletedAt = DateTime.UtcNow, CanCommit = false };
            batch.CompletedAt = report.CompletedAt;
            batch.ReportJson = JsonSerializer.Serialize(report, Json);
            await m_session.FlushAsync(ct);
            await DatabaseStatistics.RefreshImportedContentAsync(m_session, ct);
            return report;
        }
        catch
        {
            // The middleware only commits after Handle returns, so reaching here means nothing was persisted:
            // any asset already written to disk in this call is safe to remove.
            foreach (var asset in createdFiles)
            {
                try
                {
                    m_attachments.Remove(asset);
                }
                catch (IOException)
                {
                    m_logger.LogWarning("Could not remove uncommitted import attachment {Id}", asset);
                }
                catch (UnauthorizedAccessException)
                {
                    m_logger.LogWarning("Could not remove uncommitted import attachment {Id}", asset);
                }
            }
            throw;
        }
    }

    /// <summary>Source tags label a newly created page only; a later import never overrides the tags people curated.</summary>
    private async Task TagAsync(Page page, IEnumerable<Guid> tagIds, UserAccount actor, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var tagId in tagIds.Distinct().Take(20))
        {
            await m_session.SaveAsync(new PageTag
            {
                Id = Guid.CreateVersion7(),
                PageId = page.Id,
                TagId = tagId,
                CreatedById = actor.Id,
                CreatedAt = now,
                ActiveFrom = now
            }, ct);
        }
    }

    private static bool FixedEquals(string expected, string? actual)
    {
        return actual is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
    }
}
