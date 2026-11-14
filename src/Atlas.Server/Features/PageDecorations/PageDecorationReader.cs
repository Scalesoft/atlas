using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.PageDecorations.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageDecorations;

/// <summary>Metadata projection for callers that already checked page visibility.</summary>
public sealed class PageDecorationReader(ISession session)
{
    public async Task<PageDecorationDto?> ReadAsync(Guid pageId, CancellationToken ct)
    {
        var row = await session.GetAsync<PageDecoration>(pageId, ct);
        return row is null ? null : Describe(row);
    }

    public async Task<IReadOnlyDictionary<Guid, PageDecorationDto>> ReadManyAsync(Guid[] pageIds, CancellationToken ct)
    {
        if (pageIds.Length == 0) return new Dictionary<Guid, PageDecorationDto>();
        // The ids travel as one text parameter, not one parameter per page: PostgreSQL takes at most 65,535 per statement, and
        // the statement keeps one shape whatever the page count.
        var rows = await session.QueryOver<PageDecoration>()
            .Where(Expression.Sql("{alias}.\"PageId\" = ANY(CAST(string_to_array(?, ',') AS uuid[]))", string.Join(',', pageIds), NHibernateUtil.String))
            .Take(pageIds.Length).ListAsync(ct);
        return rows.ToDictionary(row => row.PageId, Describe);
    }

    public static PageDecorationDto Describe(PageDecoration row) => new(row.RevisionId, row.Icon, row.Color);
}
