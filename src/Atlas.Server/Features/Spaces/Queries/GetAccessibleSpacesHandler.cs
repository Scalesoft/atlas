using NHibernate;
using NHibernate.Criterion;
using NHibernate.SqlCommand;
using Atlas.Server.Features.SpaceCovers.Contracts;
using Atlas.Server.Features.SpaceCovers.Domain;
using Atlas.Server.Features.Spaces.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Spaces.Queries;

public sealed class GetAccessibleSpacesHandler
{
    // Deliberately two statements: legacy bounded this in one SQL statement (LEFT JOIN + GROUP BY +
    // ORDER BY + LIMIT 200), and split in two the granted space ids round-trip as an IN (...) list.
    // GrantQueryLimits chunks that list, so the ordering and the LIMIT below still see every grant.
    private const int MaxSpaces = 200;
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly SpaceRoles m_roles;
    private readonly PageListing m_listing;

    public GetAccessibleSpacesHandler(ISession session, IContentAccess access, SpaceRoles roles, PageListing listing)
    {
        m_session = session;
        m_access = access;
        m_roles = roles;
        m_listing = listing;
    }

    public async Task<AccessibleSpaces> Handle(GetAccessibleSpaces query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(null, ct);
        var visible = await ListedSpaces.ReadAsync(m_roles, m_listing, actor, ct);
        if (visible is { Count: 0 })
        {
            return new AccessibleSpaces([]);
        }
        var actorId = actor.Id;
        Space space = null!;
        SpacePin pin = null!;
        SpaceCover cover = null!;
        // The account's current pin choice and the current image state come in the same statement.
        var spaces = m_session.QueryOver(() => space)
            .JoinEntityAlias(() => pin, () => pin.SpaceId == space.Id && pin.UserId == actorId && pin.ActiveTo == null, JoinType.LeftOuterJoin)
            .JoinEntityAlias(() => cover, () => cover.SpaceId == space.Id && cover.ActiveTo == null, JoinType.LeftOuterJoin)
            .Where(() => space.TrashedAt == null);
        if (visible is not null)
        {
            spaces = spaces.And(GrantQueryLimits.InChunks(Projections.Property(() => space.Id), visible.Keys.ToList()));
        }
        var rows = await spaces
            .Select(Projections.Property(() => space.Id), Projections.Property(() => space.Name), Projections.Property(() => space.Icon),
                Projections.Property(() => space.Color), Projections.Property(() => space.Description), Projections.Property(() => space.PinPolicy),
                Projections.Property(() => pin.Pinned), Projections.Property(() => cover.Sha256), Projections.Property(() => cover.Tinted),
                Projections.Property(() => cover.Source), Projections.Property(() => cover.CatalogKey))
            .OrderBy(() => space.Name).Asc
            .ThenBy(() => space.Id).Asc
            .Take(MaxSpaces)
            .ListAsync<object[]>(ct);
        var pages = await ReadPagesAsync(actor, rows.Select(row => (Guid)row[0]).ToList(), ct);
        var items = rows.Select(row =>
            {
                var id = (Guid)row[0];
                var policy = (SpacePinPolicy)row[5];
                var (count, changedAt) = pages.GetValueOrDefault(id);
                var item = ListedSpaces.Role(visible, id) is { } role
                    ? new SpaceListItem(id, (string)row[1], (string)row[2], (string)row[3], role.ToString(), (string)row[4])
                    : new SpaceListItem(id, (string)row[1], (string)row[2], (string)row[3], nameof(SpaceRole.Reader), "") { ItemAccessOnly = true };
                return item with
                {
                    PinPolicy = policy.ToString(), Pinned = SpacePin.Effective(policy, row[6] as bool?), PageCount = count, LastChangedAt = changedAt,
                    // The automatic pick reads the description the account sees (item.Description, empty for ItemAccessOnly),
                    // so a drawing never tells what a description it may not read says.
                    Cover = SpaceCoverDto.From(row[9] as SpaceCoverSource?, row[10] as string, row[7] as string, row[8] as bool?,
                        item.Name, item.Description, item.Icon)
                };
            })
            .ToArray();
        return new AccessibleSpaces(items);
    }

    /// <summary>One grouped statement for all listed spaces, with the tree's rule (EffectivePageAccess and PageListing, no trash, no
    /// templates): the page count and the newest revision the account may read, the working one where it edits the page and the
    /// published one otherwise, so neither an unpublished edit nor a page it cannot list shows through.</summary>
    private async Task<Dictionary<Guid, (int Count, DateTime? ChangedAt)>> ReadPagesAsync(UserAccount actor, IList<Guid> spaceIds,
        CancellationToken ct)
    {
        if (spaceIds.Count == 0)
        {
            return [];
        }
        var actorId = actor.Id;
        SpacePageEntry entry = null!;
        PageAccessEntry access = null!;
        var role = Projections.Property(() => access.Role);
        var changedAt = Projections.Conditional(Restrictions.Ge(role, 2),
            Projections.Property(() => entry.DraftActiveFrom), Projections.Property(() => entry.PublishedActiveFrom));
        var rows = await m_session.QueryOver(() => entry)
            .JoinEntityAlias(() => access, () => access.PageId == entry.Id && access.UserId == actorId)
            .Where(() => !entry.IsTrashed && !entry.IsTemplate)
            .And(GrantQueryLimits.InChunks(Projections.Property(() => entry.SpaceId), spaceIds))
            .And(PageListing.Listed(Projections.Property(() => entry.IsPublished), Projections.Property(() => entry.OwnerId), role, actor))
            .Select(Projections.Group(() => entry.SpaceId), Projections.RowCount(), Projections.Max(changedAt))
            .ListAsync<object[]>(ct);
        return rows.ToDictionary(row => (Guid)row[0], row => ((int)row[1], (DateTime?)row[2]));
    }
}
