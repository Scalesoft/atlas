using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.Pages;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Linking;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Features.Search;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Commands;

public sealed class MovePageToSpaceHandler
{
    private const int MaximumPages = 1000;
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_atlasLocks;
    private readonly CollaborationCheckpoints m_checkpoints;
    private readonly ContentParticipants m_participants;
    private readonly IJobRequests m_jobs;
    private readonly Outbox m_outbox;

    public MovePageToSpaceHandler(ISession session, IContentAccess access, IAtlasLocks atlasLocks, CollaborationCheckpoints checkpoints,
        ContentParticipants participants, IJobRequests jobs, Outbox outbox)
    {
        m_session = session;
        m_access = access;
        m_atlasLocks = atlasLocks;
        m_checkpoints = checkpoints;
        m_outbox = outbox;
        m_participants = participants;
        m_jobs = jobs;
    }

    public async Task<CrossSpaceChange> Handle(MovePageToSpace command, CancellationToken ct)
    {
        var request = command.Request;
        if (!request.AcknowledgeAccessChange || request.TargetSpaceId == Guid.Empty)
        {
            throw new ApiException(400, "Potvrďte změnu přístupových práv cílového prostoru.");
        }
        var pageId = command.PageId;
        await m_atlasLocks.AcquireContentSharedAsync(ct);
        var source = await m_session.QueryOver<Page>().Where(x => x.Id == pageId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
        if (source == Guid.Empty)
        {
            throw OrganizationLocks.Missing();
        }
        // Global -> both spaces in deterministic order -> actor -> pages -> live state.
        foreach (var space in new[] { source, request.TargetSpaceId }.Distinct().Order())
        {
            if (!await m_atlasLocks.LockSpaceForUpdateAsync(space, ct))
            {
                throw OrganizationLocks.Missing();
            }
        }
        // Both spaces are held FOR UPDATE; passing them adds compatible FOR SHARE locks and lets the page decisions see them locked.
        var actor = await m_access.LockActorInSpacesAsync([source, request.TargetSpaceId], ct);
        var rootPage = await m_session.GetForUpdateAsync<Page>(pageId, ct) ?? throw OrganizationLocks.Missing();
        if (rootPage.IsTrashed)
        {
            throw OrganizationLocks.Missing();
        }
        if (rootPage.SpaceId != source)
        {
            throw new ApiException(409, "Stránka byla přesunuta. Načtěte strom znovu.", "space_changed");
        }
        await m_access.RequirePageAsync(rootPage, AccessOperation.EditContent, ct);
        // The moved pages take the target space's default access, so the target stays a space-level check. After the page's own decision:
        // the 403 of a target the account only reads must not tell it that a page it cannot see exists (review S3).
        await m_access.RequireSpaceAsync(request.TargetSpaceId, SpaceRole.Writer, ct);
        // As in 1.0 the pages leave the source space, which is a space operation too: Writer there, besides EditContent on each page.
        await m_access.RequireSpaceAsync(source, SpaceRole.Writer, ct);
        if (source == request.TargetSpaceId)
        {
            throw new ApiException(400, "Pro změnu umístění v témže prostoru použijte přesun ve stromu.");
        }
        await TreeVersion.ExpectAsync(m_session, source, request.ExpectedSourceTreeVersion, ct);
        await TreeVersion.ExpectAsync(m_session, request.TargetSpaceId, request.ExpectedTargetTreeVersion, ct);
        var ids = await PageSubtree.ReachableAsync(m_session, pageId, MaximumPages + 1, ct);
        if (ids.Count == 0)
        {
            throw OrganizationLocks.Missing();
        }
        var pages = await PageSubtree.LockSelectionAsync(m_session, m_access, m_atlasLocks, ids.Order().ToArray(), AccessOperation.EditContent, ct);
        if (pages.Any(x => x.SpaceId != source))
        {
            throw new ApiException(409, "Stránka byla přesunuta. Načtěte strom znovu.", "space_changed");
        }
        if (ids.Count > MaximumPages)
        {
            throw new ApiException(409, "Přesun podporuje nejvýše 1000 stránek. Rozdělte podstrom na menší části.");
        }
        if (pages.Any(x => x.IsTrashed))
        {
            throw new ApiException(409, "Nejprve obnovte podstránky v koši nebo je přesuňte mimo tento podstrom.");
        }
        if (!request.IncludeSubtree && pages.Count > 1)
        {
            throw new ApiException(409, "Potvrďte přesun všech podstránek.", "subtree_confirmation_required");
        }
        var page = pages.Single(x => x.Id == pageId);
        Rules.ExpectVersion(page.Version, request.ExpectedVersion);
        // The Page locks exclude every append/checkpoint writer.
        if (await m_session.QueryOver<PageCollaboration>().Where(GuidList.In("PageId", ids))
                .And(Expression.Sql("{alias}.\"LastSavedSequence\" IS DISTINCT FROM {alias}.\"Sequence\"")).RowCountAsync(ct) > 0)
        {
            throw new ApiException(409, "Nejprve uložte společné úpravy všech přesouvaných stránek.", "collaboration_unsaved");
        }
        // Validate against a detached destination projection, avoiding an intermediate automatic flush.
        var destination = new Page { Id = pageId, SpaceId = request.TargetSpaceId };
        await PageParent.ValidateAsync(m_session, m_access, destination, request.ParentId, ct);
        var order = await PageSiblings.PositionAsync(m_session, m_participants.Tree, request.TargetSpaceId, pageId, request.ParentId, request.BeforePageId, actor, ct);
        page.ParentId = request.ParentId;
        page.SortOrder = order;
        foreach (var item in pages)
        {
            item.SpaceId = request.TargetSpaceId;
            // Nothing new to tell anyone: the derived rows that follow the space (mentions matched by e-mail, index tasks) rebuild from the page.
            m_outbox.ContentRebuilt(item.Id, PagesAtlasModule.ContentType);
        }
        // The other content types' leaves under the moved pages, trashed or not, go along (interaction spec 6).
        foreach (var participant in m_participants.Tree)
        {
            await participant.MoveSpaceAsync(source, request.TargetSpaceId, ids, ct);
        }
        // A page in another space ends its share links (they were issued under the old space's policy and authority).
        await ShareRevocation.PagesAsync(m_session, [.. pages.Select(x => x.Id)], actor.Id, ct);
        // D7: the moved subtree takes the target space's default access; its own-access mode and item grants end here.
        await PageItemAccess.ResetAsync(m_session, pages, actor.Id, ct);
        await PageVerifications.Commands.PageVerificationArrival.StartAsync(m_session, pages, actor.Id, ct);
        await m_checkpoints.ResetAllAsync(ids, ct);
        await SearchDocuments.RefreshPublishedAsync(m_session, ids, ct);
        // The moved pages' mentions were read with the old space's names, and both spaces' names changed (ContentReferenceJob).
        await ContentReferenceJob.RequestAsync(m_jobs, "Přesun stránek do jiného prostoru", ct);
        return new CrossSpaceChange(source, request.TargetSpaceId,
            await TreeVersion.ReadAsync(m_session, source, ct), await TreeVersion.ReadAsync(m_session, request.TargetSpaceId, ct), ids.ToArray());
    }
}
