using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Spaces.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageTemplates;

public sealed class ListPageTemplatesHandler(ISession session, IContentAccess access, PageListing listing)
{
    public async Task<IReadOnlyList<PageTemplateSummary>> Handle(ListPageTemplates query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(query.SpaceId, ct);
        await listing.RequireSpaceAsync(actor, query.SpaceId, ct);
        var actorId = actor.Id;
        SpacePageEntry page = null!;
        PageAccessEntry permission = null!;
        var rows = await session.QueryOver(() => page)
            .JoinEntityAlias(() => permission, () => permission.PageId == page.Id && permission.UserId == actorId)
            .Where(() => page.SpaceId == query.SpaceId && page.IsTemplate && !page.IsTrashed)
            .And(PageListing.Listed(Projections.Property(() => page.IsPublished), Projections.Property(() => page.OwnerId),
                Projections.Property(() => permission.Role), actor))
            .Select(Projections.Property(() => page.Id), Projections.Property(() => page.ParentId),
                Projections.Property(() => page.DraftTitle), Projections.Property(() => page.PublishedTitle),
                Projections.Property(() => page.Version), Projections.Property(() => page.IsPublished),
                Projections.Property(() => permission.Role))
            .OrderBy(() => page.SortOrder).Asc.ThenBy(() => page.Id).Asc
            .Take(1000).ListAsync<object[]>(ct);
        return rows.Select(row => new PageTemplateSummary((Guid)row[0], query.SpaceId, (Guid?)row[1],
            ((int)row[6] >= 2 ? row[2] as string : null) ?? (string)row[3], (int)row[4], (bool)row[5])).ToArray();
    }
}
