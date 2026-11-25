using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageOrganization.Queries;

public sealed class GetPageTreeHandler
{
    private readonly ISession m_session;
    private readonly OrganizationLocks m_locks;
    private readonly PageListing m_listing;
    private readonly PageDecorationReader m_decorations;
    private readonly ContentParticipants m_participants;
    private readonly PageVerifications.PageVerificationReader m_verifications;

    public GetPageTreeHandler(ISession session, OrganizationLocks locks, PageListing listing, PageDecorationReader decorations, ContentParticipants participants,
        PageVerifications.PageVerificationReader verifications)
    {
        m_session = session;
        m_locks = locks;
        m_listing = listing;
        m_decorations = decorations;
        m_participants = participants;
        m_verifications = verifications;
    }

    public async Task<PageTreeDto> Handle(GetPageTree query, CancellationToken ct)
    {
        var spaceId = query.SpaceId;
        var actor = await m_locks.LockForReadAsync(spaceId, ct);
        var role = await m_listing.RequireSpaceAsync(actor, spaceId, ct);
        // Every page is decided on its own (EffectivePageAccess); narrow projections keep document bodies and history out.
        var actorId = actor.Id;
        PageTreeEntry entry = null!;
        PageAccessEntry access = null!;
        var rows = await m_session.QueryOver(() => entry)
            .JoinEntityAlias(() => access, () => access.PageId == entry.Id && access.UserId == actorId)
            .Where(() => entry.SpaceId == spaceId && !entry.IsTrashed && !entry.IsTemplate)
            .And(PageListing.Listed(Projections.Property(() => entry.IsPublished), Projections.Property(() => entry.OwnerId),
                Projections.Property(() => access.Role), actor))
            .Select(Projections.Property(() => entry.Id), Projections.Property(() => entry.ParentId),
                Projections.Property(() => entry.WorkingTitle), Projections.Property(() => entry.PublishedTitle),
                Projections.Property(() => entry.Version), Projections.Property(() => entry.SortOrder),
                Projections.Property(() => entry.IsPublished), Projections.Property(() => access.Role), Projections.Property(() => entry.SortChildrenByTitle))
            .OrderBy(() => entry.SortOrder).Asc.ThenBy(() => entry.Id).Asc
            .ListAsync<object[]>(ct);
        var visible = rows.Select(row => (Guid)row[0]).ToHashSet();
        var decorations = await m_decorations.ReadManyAsync(visible.ToArray(), ct);
        var verifications = await m_verifications.StatesInSpaceAsync(spaceId, ct);
        // D8: a page whose parent is hidden is shown at the root, without a placeholder for the parent.
        var items = rows.Select(row =>
        {
            var id = (Guid)row[0];
            var canEdit = (int)row[7] >= 2;
            return new PageTreeItem(id, row[1] is Guid parent && visible.Contains(parent) ? parent : null,
                canEdit ? (string)row[2] : (string)row[3], (int)row[4], (int)row[5], (bool)row[6], canEdit)
            {
                Decoration = decorations.GetValueOrDefault(id), ChildrenByTitle = (bool)row[8], Verification = verifications.GetValueOrDefault(id)
            };
        }).ToList();
        // The other content types' leaves (interaction spec 6), one query each, in the one sibling order; a hidden parent puts
        // them at the root (D8).
        var attachedBoards = new Dictionary<Guid, ContentTreeNode>();
        foreach (var participant in m_participants.Tree)
        {
            var nodes = await participant.NodesAsync(spaceId, actor, role, ct);
            foreach (var attached in nodes.Where(node => node.Type == ContentTreeNode.Attached && node.ParentId is not null))
            {
                attachedBoards[attached.ParentId!.Value] = attached;
            }
            items.AddRange(nodes.Where(node => node.Type != ContentTreeNode.Attached)
                .Select(node => PageTreeItem.Of(node, node.ParentId is { } parent && visible.Contains(parent) ? parent : null)));
        }
        items.Sort((a, b) => PageSiblings.Compare(a.SortOrder, a.Id, b.SortOrder, b.Id));
        // The tree's version and the root's „Řadit abecedně“ in one read; a pinned level is shown by the titles this account sees.
        var organization = await m_session.QueryOver<SpaceOrganization>().Where(x => x.SpaceId == spaceId)
            .Select(x => x.Version, x => x.SortRootByTitle).SingleOrDefaultAsync<object[]>(ct);
        var rootByTitle = organization is not null && (bool)organization[1];
        var pinned = items.Where(item => item.ChildrenByTitle).Select(item => (Guid?)item.Id).ToHashSet();
        if (rootByTitle)
        {
            pinned.Add(null);
        }
        TitleOrder.Arrange(items, pinned);
        // A page's own board: its page's editors open it, its readers too when it is Readable (BoardAccess).
        var tree = items.Select(item => item.Type == "page" && attachedBoards.TryGetValue(item.Id, out var board) && (item.CanEdit || board.Readable)
            ? item with { AttachedBoardId = board.Id } : item).ToList();
        return new PageTreeDto(spaceId, organization is null ? 0 : (long)organization[0], tree) { RootByTitle = rootByTitle };
    }
}
