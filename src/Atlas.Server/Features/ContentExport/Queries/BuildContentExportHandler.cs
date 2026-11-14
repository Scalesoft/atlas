using Atlas.Server.Features.Vaults.References;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.ContentExport.Contracts;
using Atlas.Server.Features.ContentExport.Domain;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Platform.Content;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ContentExport.Queries;

/// <summary>
/// Deviation from the plan text (driver decision, recorded as Global Constraints deviation 1 in the wave 2
/// plan): building the archive does not hold any lock for the whole ZIP build. It resolves the actor through
/// <see cref="ICurrentActor"/> and the listed pages through <see cref="PageListing"/> (a whole space)
/// or ReadContent through <see cref="ResourceGrantSnapshots"/> (one page) instead of going through <see cref="IContentAccess"/>, so the shared advisory lock and the UserAccount/Space FOR SHARE
/// locks are never held while streaming pages and attachment blobs into the ZIP. The real guarantee is the
/// separate, deliberately fresh <see cref="RevalidateContentExport"/> transaction, which does take those locks
/// through IContentAccess before the archive is allowed to leave the server. The middleware transaction here is
/// ReadCommitted; legacy ran the build under RepeatableRead. Legacy already documented the same asymmetry on the
/// revalidation step: "This fresh transaction deliberately does not reuse the content snapshot."
/// </summary>
public sealed class BuildContentExportHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ISession m_session;
    private readonly ICurrentActor m_currentActor;
    private readonly PageListing m_listing;
    private readonly ResourceGrantSnapshots m_grants;
    private readonly AttachmentFiles m_attachments;
    private readonly ContentExportFiles m_files;
    private readonly SecretReferenceAccess m_references;
    private readonly PageLinkStore m_links;

    private readonly Pages.Domain.RequirementPageConversion m_conversion;

    public BuildContentExportHandler(ISession session, ICurrentActor currentActor, PageListing listing, ResourceGrantSnapshots grants,
        AttachmentFiles attachments, ContentExportFiles files, SecretReferenceAccess references, PageLinkStore links,
        Pages.Domain.RequirementPageConversion conversion)
    {
        m_conversion = conversion;
        m_references = references;
        m_links = links;
        m_session = session;
        m_currentActor = currentActor;
        m_listing = listing;
        m_grants = grants;
        m_attachments = attachments;
        m_files = files;
    }

    public async Task<ContentExportArchive> Handle(BuildContentExport query, CancellationToken ct)
        => await BuildForActorAsync(query, await m_currentActor.RequireAsync(ct), ct);

    /// <param name="audience">Who reads the result outside the wiki (the automatic PDF export's storage); null for the reader's own download.</param>
    internal async Task<ContentExportArchive> BuildForActorAsync(BuildContentExport query, UserAccount requestedActor, CancellationToken ct,
        OutboundAudience? audience = null)
    {
        if (query.WholeSpace && query.RevisionId is not null) throw new ApiException(400, "Konkrétní revizi lze exportovat jen pro jednotlivou stránku.");
        // Build privately before sending headers, so limits, corruption and revoked permissions fail atomically.
        var output = m_files.Create();
        try
        {
            var selected = new List<ExportSelection>();
            var payloads = new List<ExportFile>();
            var exportedPages = new List<ExportPage>();
            var exportedAttachments = new List<ExportAttachment>();
            var budget = new ExportBudget();
            var currentActor = await RequireFreshActorAsync(requestedActor, ct);
            var singlePage = query.WholeSpace ? null : await m_session.GetAsync<Page>(query.Id, ct) ?? throw Missing();
            var spaceId = query.WholeSpace ? query.Id : singlePage!.SpaceId;
            // A whole space is a list: its listed pages, each with the revision the account may read (WP3 S3). One page is
            // ReadContent on that page.
            if (singlePage is null)
            {
                await m_listing.RequireSpaceAsync(currentActor, spaceId, ct);
            }
            var pages = singlePage is null
                ? await SelectSpaceAsync(spaceId, currentActor, ct)
                : SelectPage(singlePage, await RequirePageAsync(currentActor, singlePage, ct), currentActor,
                    await EffectiveWikiAdmin.IsAdminAsync(m_session, currentActor, ct));
            var space = await m_session.GetAsync<Space>(spaceId, ct);
            if (!query.WholeSpace && pages.Count == 0)
            {
                throw Missing();
            }
            if (pages.Count > ExportBudget.MaximumPages)
            {
                throw ExportBudget.TooLarge();
            }
            // D8: in a space export a parent the account cannot list is left out, as the tree shows such a page at the root.
            var listed = pages.Select(x => x.Page.Id).ToHashSet();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                foreach (var (page, canEdit) in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    if (query.RevisionId is not null && !canEdit) throw new ApiException(403, "Historické revize může exportovat pouze editor stránky.");
                    var revision = query.RevisionId is { } requestedRevision
                        ? await m_session.GetAsync<PageRevision>(requestedRevision, ct)
                        : canEdit
                        ? await m_session.QueryOver<PageRevision>().Where(r => r.PageId == page.Id && r.ActiveTo == null).SingleOrDefaultAsync(ct)
                        : await m_session.GetAsync<PageRevision>(page.PublishedRevisionId!.Value, ct);
                    if (revision is null || revision.PageId != page.Id)
                    {
                        throw new ApiException(409, "Verze článku není dostupná. Zkuste export znovu.");
                    }
                    // 3.6 repair on open for the current revision (a chosen history revision stays as stored).
                    if (query.RevisionId is null && await m_conversion.RepairOnOpenAsync(page.Id, revision.Id, revision.ContentJson, ct))
                    {
                        await m_session.RefreshAsync(revision, ct);
                    }
                    selected.Add(new ExportSelection(page.Id, spaceId, revision.Id));
                    var directory = $"pages/{page.Id:D}";
                    var jsonPath = directory + "/content.json";
                    var markdownPath = directory + "/content.md";
                    // Only references the exporting account may Discover stay (never a value: documents hold none), in the JSON and as
                    // Markdown directives alike.
                    var contentJson = (await m_references.ForReaderAsync(currentActor, revision.ContentJson, ct)).Json;
                    if (audience is { } outbound)
                    {
                        contentJson = OutboundDocument.ForAudience(contentJson, outbound, await ReadableLinksAsync(currentActor, revision.Id, ct));
                    }
                    var markdown = await m_references.MarkdownForReaderAsync(currentActor, revision.Markdown, ct);
                    payloads.Add(await ExportWriter.WriteTextAsync(zip, jsonPath, contentJson, "application/json", budget, ct));
                    payloads.Add(await ExportWriter.WriteTextAsync(zip, markdownPath, markdown, "text/markdown; charset=utf-8", budget, ct));
                    var parentId = query.WholeSpace && page.ParentId is { } parent && !listed.Contains(parent) ? null : page.ParentId;
                    exportedPages.Add(new ExportPage(page.Id, spaceId, parentId, page.SortOrder, $"/#/pages/{page.Id:D}", revision.Id, revision.Version,
                        revision.Title, revision.ActiveFrom, revision.ActiveTo, revision.Id == page.PublishedRevisionId, jsonPath, markdownPath));
                    var revisionId = revision.Id;
                    var attachmentIds = await m_session.QueryOver<PageRevisionAttachment>()
                        .Where(x => x.RevisionId == revisionId)
                        .Select(x => x.AttachmentId)
                        .OrderBy(x => x.AttachmentId).Asc
                        .ListAsync<Guid>(ct);
                    foreach (var attachmentId in attachmentIds)
                    {
                        var attachment = await m_session.GetAsync<Attachment>(attachmentId, ct);
                        if (attachment is null || attachment.PageId != page.Id || attachment.ActiveTo is not null)
                        {
                            throw new ApiException(409, "Příloha exportované verze není dostupná.");
                        }
                        var path = $"attachments/{attachment.Id:D}/blob";
                        try
                        {
                            await using var input = m_attachments.Open(attachment.Id);
                            var entry = await ExportWriter.WriteAsync(zip, path, input, attachment.MimeType, budget, ct);
                            if (entry.Size != attachment.Size || entry.Sha256 != attachment.Sha256)
                            {
                                throw new ApiException(409, "Kontrola integrity přílohy selhala. Správce musí ověřit úložiště.");
                            }
                            payloads.Add(entry);
                        }
                        catch (FileNotFoundException)
                        {
                            throw new ApiException(409, "Soubor přílohy chybí. Správce musí ověřit úložiště.");
                        }
                        catch (DirectoryNotFoundException)
                        {
                            throw new ApiException(409, "Úložiště příloh není dostupné.");
                        }
                        exportedAttachments.Add(new ExportAttachment(attachment.Id, page.Id, attachment.Name, attachment.MimeType, attachment.Size,
                            attachment.Sha256, AttachmentUrls.Describe(attachment).Url, path));
                        await m_session.EvictAsync(attachment, ct);
                    }
                    // Rich content is bounded per revision; do not retain the entire space in the NHibernate first-level cache.
                    await m_session.EvictAsync(revision, ct);
                }
                var manifest = new
                {
                    format = "scaleatlas-content-export", version = 1, exportedAt = DateTime.UtcNow,
                    scope = new { type = query.WholeSpace ? "space" : "page", id = query.Id },
                    space = new { space.Id, space.Name, space.Icon, space.Color },
                    pages = exportedPages, attachments = exportedAttachments, files = payloads,
                    integrity = "SHA-256 covers every payload file. The response ETag covers the complete ZIP, including this manifest.",
                    limits = new { maximumBytes = ExportBudget.MaximumBytes, maximumPages = ExportBudget.MaximumPages, maximumFiles = ExportBudget.MaximumFiles },
                    content = "Saved article revisions only; live collaboration changes must be saved before export. External URLs are preserved as links and are not fetched. This archive is not a complete server backup."
                };
                await ExportWriter.WriteTextAsync(zip, "manifest.json", JsonSerializer.Serialize(manifest, Json), "application/json", budget, ct);
            }
            await output.FlushAsync(ct);
            output.Position = 0;
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(output, ct));
            output.Position = 0;
            var name = query.WholeSpace ? ContentExportNames.Stem(space.Name, "atlas-space") : ContentExportNames.Stem(exportedPages[0].Title, "atlas-page");
            return new ContentExportArchive(output, name + ".zip", hash, spaceId, selected);
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    /// <summary>The pages the revision links to that the account may open, with the title it would open (as MCP shows them).</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ReadableLinksAsync(UserAccount actor, Guid revisionId, CancellationToken ct)
    {
        var content = await m_session.CreateSQLQuery("""SELECT "ContentJson" AS json FROM "PageRevision" WHERE "Id"=:revision""")
            .AddScalar("json", NHibernateUtil.String).SetParameter("revision", revisionId).UniqueResultAsync<string?>(ct);
        var targets = PageLinkTargets.Of(content);
        return targets.Count == 0 ? new Dictionary<Guid, string>()
            : (await m_links.ResolveAsync(actor, targets.ToArray(), ct)).ToDictionary(page => page.Id, page => page.Title);
    }

    private static IReadOnlyList<(Page Page, bool CanEdit)> SelectPage(Page page, bool canEdit, UserAccount actor, bool wikiAdmin)
    {
        var visible = !page.IsTrashed && (page.PublishedRevisionId is not null || canEdit && (page.OwnerId == actor.Id || wikiAdmin));
        return visible ? [(page, canEdit)] : [];
    }

    /// <summary>The listed pages of the space (PageListing), each with whether the account edits it (working revision) or only
    /// reads it (published revision). A page the account cannot read contributes nothing, not even its title.</summary>
    private async Task<IReadOnlyList<(Page Page, bool CanEdit)>> SelectSpaceAsync(Guid spaceId, UserAccount actor, CancellationToken ct)
    {
        var actorId = actor.Id;
        Page page = null!;
        PageAccessEntry access = null!;
        var rows = await m_session.QueryOver(() => page)
            .JoinEntityAlias(() => access, () => access.PageId == page.Id && access.UserId == actorId)
            .Where(() => page.SpaceId == spaceId && !page.IsTrashed && !page.IsTemplate)
            .And(PageListing.Listed(Projections.Property(() => access.IsPublished), Projections.Property(() => access.OwnerId),
                Projections.Property(() => access.Role), actor))
            .SelectList(list => list.Select(Projections.RootEntity()).Select(() => access.Role))
            .OrderBy(() => page.Id).Asc
            .Take(ExportBudget.MaximumPages + 1)
            .ListAsync<object[]>(ct);
        return rows.Select(row => ((Page)row[0], (int)row[1] >= 2)).ToArray();
    }

    /// <summary>
    /// Same freshness check as ContentAccessService.LockActorAsync (and the legacy ContentExportService's own
    /// RequireActorAsync), minus the locks: ICurrentActor alone would be enough in production (HttpCurrentActor
    /// already re-reads the account row per request), but the account may have been demoted or deactivated by a
    /// concurrent write since HttpCurrentActor cached it, and the test double behind ICurrentActor does not
    /// re-read at all. Re-fetching and refreshing here is what keeps IsWikiAdmin/IsActive/SecurityStamp current
    /// without taking IContentAccess's advisory or row locks.
    /// </summary>
    private async Task<UserAccount> RequireFreshActorAsync(UserAccount requested, CancellationToken ct)
    {
        var current = await m_session.GetAsync<UserAccount>(requested.Id, ct);
        if (current is not null)
        {
            await m_session.RefreshAsync(current, ct);
        }
        if (current is null || !current.IsActive || current.SecurityStamp != requested.SecurityStamp)
        {
            throw new ApiException(401, "Přihlášení vypršelo.");
        }
        return current;
    }

    /// <summary>
    /// ReadContent on one page, lock-free: the same page decision as IContentAccess, all denials answered as missing content.
    /// Returns whether the actor may edit the page.
    /// </summary>
    private async Task<bool> RequirePageAsync(UserAccount actor, Page page, CancellationToken ct)
    {
        var authorization = await m_grants.AuthorizePageAsync(actor, page, AccessOperation.ReadContent, ct);
        return authorization.Allowed ? authorization.CanEdit : throw Missing();
    }

    private static ApiException Missing()
    {
        return new ApiException(404, "Obsah nebyl nalezen nebo k němu nemáte přístup.");
    }
}
