using NHibernate;
using NHibernate.Criterion;
using NHibernate.SqlCommand;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Queries;

public sealed class GetSpaceDraftsHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;

    public GetSpaceDraftsHandler(ISession session, IContentAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<SpaceDraftList> Handle(GetSpaceDrafts query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(query.SpaceId, ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "Koncepty může schvalovat pouze správce wiki.");
        }
        await m_access.RequireSpaceAsync(query.SpaceId, SpaceRole.Admin, ct);
        ValidateFilters(query);

        Page page = null!;
        PageRevision current = null!;
        PageRevision published = null!;
        UserAccount author = null!;
        // Project metadata only; bodies are loaded on demand through the revision preview API.
        var rows = m_session.QueryOver(() => page)
            .JoinEntityAlias(() => current, () => current.PageId == page.Id && current.ActiveTo == null)
            .JoinEntityAlias(() => published, () => published.Id == page.PublishedRevisionId, JoinType.LeftOuterJoin)
            .JoinEntityAlias(() => author, () => author.Id == current.AuthorId)
            .Where(() => page.SpaceId == query.SpaceId && !page.IsTrashed && !page.IsTemplate)
            .And(Restrictions.Or(
                Restrictions.IsNull(Projections.Property(() => page.PublishedRevisionId)),
                Restrictions.NotEqProperty(Projections.Property(() => current.Id), Projections.Property(() => page.PublishedRevisionId))));
        // Authors span the space's entire queue, not only the currently loaded page or filter.
        var authorRows = await rows.Clone()
            .Select(Projections.Distinct(Projections.ProjectionList()
                .Add(Projections.Property(() => author.Id)).Add(Projections.Property(() => author.DisplayName))))
            .OrderBy(() => author.DisplayName).Asc.ThenBy(() => author.Id).Asc.ListAsync<object[]>(ct);
        var authors = authorRows.Select(row => new SpaceDraftAuthor((Guid)row[0], (string)row[1])).ToArray();
        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            rows = rows.And(Restrictions.InsensitiveLike(Projections.Property(() => current.Title), query.Query.Trim(), MatchMode.Anywhere));
        }
        if (query.PageId is { } pageId)
        {
            // One row read again after its approval answered draft_changed (autosave moved the draft on).
            rows = rows.And(() => page.Id == pageId);
        }
        if (query.AuthorId is { } authorId)
        {
            rows = rows.And(() => current.AuthorId == authorId);
        }
        if (query.Kind == "new")
        {
            rows = rows.And(() => page.PublishedRevisionId == null);
        }
        else if (query.Kind == "changes")
        {
            rows = rows.And(() => page.PublishedRevisionId != null);
        }

        var total = await rows.Clone().RowCountAsync(ct);
        var result = await rows.Select(
                Projections.Property(() => page.Id), Projections.Property(() => page.SpaceId),
                Projections.Property(() => current.Title), Projections.Property(() => page.Version),
                Projections.Property(() => current.Id), Projections.Property(() => page.PublishedRevisionId),
                Projections.Property(() => published.Version), Projections.Property(() => current.AuthorId),
                Projections.Property(() => author.DisplayName), Projections.Property(() => current.ActiveFrom))
            .OrderBy(() => current.ActiveFrom).Desc.ThenBy(() => page.Id).Asc
            .Skip(query.Offset).Take(query.Limit).ListAsync<object[]>(ct);
        var items = result.Select(row => new SpaceDraftItem((Guid)row[0], (Guid)row[1], (string)row[2],
            (int)row[3], (Guid)row[4], (Guid?)row[5], (int?)row[6], (Guid)row[7], (string)row[8], (DateTime)row[9])).ToArray();
        return new SpaceDraftList(items, total, authors);
    }

    private static void ValidateFilters(GetSpaceDrafts query)
    {
        if (query.Kind is not ("all" or "new" or "changes") || query.Offset < 0 || query.Limit is < 1 or > 100 || query.Query?.Length > 200)
        {
            throw new ApiException(400, "Neplatný filtr konceptů. Počet položek musí být 1–100, posun nezáporný a hledaný název nejvýše 200 znaků.");
        }
    }
}
