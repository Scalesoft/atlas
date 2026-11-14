using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Commands;

public sealed class UntrashPageHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly OrganizationLocks m_locks;
    private readonly IAtlasLocks m_atlasLocks;
    private readonly ContentParticipants m_participants;
    private readonly Outbox m_outbox;

    public UntrashPageHandler(ISession session, IContentAccess access, OrganizationLocks locks, IAtlasLocks atlasLocks, ContentParticipants participants, Outbox outbox)
    {
        m_outbox = outbox;
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_atlasLocks = atlasLocks;
        m_participants = participants;
    }

    public async Task<OrganizationChange> Handle(UntrashPage command, CancellationToken ct)
    {
        var request = command.Request;
        // Restoring from the trash stays with space administration as in 1.0 (the trash is listed only to space Admins): ManageAccess,
        // which the space Admin holds on every page of the space (D4), and the space role itself, which a page's manage grant is not.
        var (page, actor) = await m_locks.LockPageAsync(command.PageId, true, AccessOperation.ManageAccess, ct);
        await OrganizationLocks.RequireSpaceAdministrationAsync(m_access, page.SpaceId, "Stránku z koše obnovuje správce prostoru.", ct);
        await TreeVersion.ExpectAsync(m_session, page.SpaceId, request.ExpectedTreeVersion, ct);
        await PageParent.ValidateAsync(m_session, m_access, page, request.ParentId, ct);
        var ids = request.IncludeSubtree ? await PageSubtree.DescendantsAsync(m_session, page, true, ct) : [page.Id];
        var pages = await PageSubtree.LockSelectionAsync(m_session, m_access, m_atlasLocks, ids, AccessOperation.ManageAccess, ct);
        var order = await PageSiblings.PositionAsync(m_session, m_participants.Tree, page.SpaceId, page.Id, request.ParentId, null, actor, ct);
        var batch = page.TrashBatchId!.Value;
        var pageId = page.Id;
        var title = await m_session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.ActiveTo == null)
            .Select(x => x.Title).SingleOrDefaultAsync<string>(ct);
        var collision = await HasSiblingTitleCollisionAsync(page.SpaceId, request.ParentId, title, actor, ct);
        page.ParentId = request.ParentId;
        page.SortOrder = order;
        PageTrash.Restore(m_outbox, pages);
        foreach (var participant in m_participants.Tree)
        {
            await participant.RestoreAsync(page.SpaceId, ids, false, batch, ct);
        }
        await m_session.FlushAsync(ct);
        return new OrganizationChange(await TreeVersion.ReadAsync(m_session, page.SpaceId, ct), ids, collision);
    }

    private async Task<bool> HasSiblingTitleCollisionAsync(Guid spaceId, Guid? parentId, string title, UserAccount actor, CancellationToken ct)
    {
        // The page being restored is still trashed here, so it can never collide with itself. Only siblings the account can
        // list count (WP3 S3), so the answer reveals nothing about a hidden page.
        var actorId = actor.Id;
        PageTreeEntry entry = null!;
        PageAccessEntry access = null!;
        var siblings = m_session.QueryOver(() => entry)
            .JoinEntityAlias(() => access, () => access.PageId == entry.Id && access.UserId == actorId)
            .Where(() => entry.SpaceId == spaceId && !entry.IsTrashed)
            .And(PageListing.Listed(Projections.Property(() => entry.IsPublished), Projections.Property(() => entry.OwnerId),
                Projections.Property(() => access.Role), actor));
        siblings = parentId is { } parent ? siblings.And(() => entry.ParentId == parent) : siblings.And(() => entry.ParentId == null);
        // The title the account sees on that sibling: working for an editor, published for a reader.
        var visibleTitle = Projections.Conditional(Restrictions.Ge(Projections.Property(() => access.Role), 2),
            Projections.Property(() => entry.WorkingTitle), Projections.Property(() => entry.PublishedTitle));
        var storedTitle = Projections.SqlFunction("lower", NHibernateUtil.String, visibleTitle);
        var requestedTitle = Projections.SqlFunction("lower", NHibernateUtil.String, Projections.Constant(title));
        return await siblings.Where(Restrictions.EqProperty(storedTitle, requestedTitle)).RowCountAsync(ct) > 0;
    }
}
