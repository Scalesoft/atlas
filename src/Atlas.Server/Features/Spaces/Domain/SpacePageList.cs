using NHibernate;
using NHibernate.Criterion;
using NHibernate.SqlCommand;
using Atlas.Server.Features.Spaces.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Spaces.Domain;

/// <summary>The flat page list of one space for the web and the personal API: listed pages only (PageListing), filtered
/// in SQL before the window is applied, each with the title the account may see on that page.</summary>
public static class SpacePageList
{
    public static async Task<IReadOnlyList<SpacePageListItem>> ReadAsync(ISession session, UserAccount actor, Guid spaceId,
        int skip, int take, CancellationToken ct)
    {
        var actorId = actor.Id;
        var wikiAdmin = await EffectiveWikiAdmin.IsAdminAsync(session, actor, ct);
        SpacePageEntry entry = null!;
        PageAccessEntry access = null!;
        PageAccessEntry parent = null!;
        var rows = await session.QueryOver(() => entry)
            .JoinEntityAlias(() => access, () => access.PageId == entry.Id && access.UserId == actorId)
            .JoinEntityAlias(() => parent, () => parent.PageId == entry.ParentId && parent.UserId == actorId, JoinType.LeftOuterJoin)
            .Where(() => entry.SpaceId == spaceId && !entry.IsTrashed && !entry.IsTemplate)
            .And(PageListing.Listed(Projections.Property(() => entry.IsPublished), Projections.Property(() => entry.OwnerId),
                Projections.Property(() => access.Role), actor))
            .Select(Projections.Property(() => entry.Id), Projections.Property(() => entry.ParentId),
                Projections.Property(() => entry.DraftTitle), Projections.Property(() => entry.PublishedTitle),
                Projections.Property(() => entry.Version), Projections.Property(() => entry.IsPublished),
                Projections.Property(() => access.Role), Projections.Property(() => parent.Role), Projections.Property(() => parent.IsTrashed),
                Projections.Property(() => parent.IsPublished), Projections.Property(() => parent.OwnerId))
            .OrderBy(() => entry.SortOrder).Asc.ThenBy(() => entry.Id).Asc
            .Skip(skip)
            .Take(take)
            .ListAsync<object[]>(ct);
        // An editor of the page reads its current revision, a reader only the published one. D8: a parent the account cannot
        // list is not revealed, the page is listed at the root as in the tree.
        return rows.Select(row => new SpacePageListItem((Guid)row[0], spaceId, ParentVisible(row, actor, wikiAdmin) ? (Guid?)row[1] : null,
                ((int)row[6] >= 2 ? row[2] as string : null) ?? (string)row[3], (int)row[4], (bool)row[5]))
            .ToArray();
    }

    private static bool ParentVisible(object[] row, UserAccount actor, bool wikiAdmin)
    {
        return row[1] is Guid && row[7] is int role && row[8] is false
            && (row[9] is true || role >= 2 && (row[10] is Guid owner && owner == actor.Id || wikiAdmin));
    }
}
