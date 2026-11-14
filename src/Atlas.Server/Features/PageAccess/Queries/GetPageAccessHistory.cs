using System.Globalization;
using NHibernate;
using Atlas.Server.Features.PageHistory.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageAccess.Queries;

public sealed record GetPageAccessHistory(Guid PageId, long? BeforeId = null, int Limit = 50);

public sealed class GetPageAccessHistoryHandler(ISession session, PageReader reader, IContentAccess access, ICurrentActor currentActor)
{
    public async Task<PageAccessHistory> Handle(GetPageAccessHistory query, CancellationToken ct)
    {
        var page = await reader.ReadAsync(query.PageId, ct);
        // The page read locked the actor and the page's space, refreshed the page and authorized ReadContent: a reader is told
        // the refusal (403). The page is in the session's first-level cache, so this is no second read.
        var locked = await session.GetAsync<Page>(page.Id, ct) ?? throw new ApiException(404, "Stránka nebyla nalezena.");
        await access.RequirePageAsync(locked, AccessOperation.ManageAccess, ct);
        if (query.BeforeId is <= 0 || query.Limit is < 1 or > 100)
            throw new ApiException(400, "Neplatné stránkování historie přístupů.");
        var rows = await session.CreateSQLQuery("""
            SELECT e."Id",e."Action",e."OccurredAt",e."ActorId",u."DisplayName",
                e."ShareLinkId",l."Name",r."Version",e."Channel",e."ApiTokenId"
            FROM "PageAccessEvent" e
            LEFT JOIN "UserAccount" u ON u."Id"=e."ActorId"
            LEFT JOIN "PageShareLink" l ON l."Id"=e."ShareLinkId"
            JOIN "PageRevision" r ON r."Id"=e."RevisionId" AND r."PageId"=e."PageId"
            WHERE e."PageId"=:page AND (:before IS NULL OR e."Id"<:before)
            ORDER BY e."Id" DESC
            """).SetParameter("page", page.Id).SetParameter("before", query.BeforeId, NHibernateUtil.Int64)
            .SetMaxResults(query.Limit + 1).ListAsync<object[]>(ct);
        var visible = rows.Take(query.Limit).ToArray();
        // The table exists with or without the projector module (migration 2026102800), so the history needs no module hook.
        var marks = query.BeforeId is null
            ? await session.CreateSQLQuery("""
                SELECT e."Id",e."IsCatalog",e."OccurredAt",e."ActorId",u."DisplayName",e."Channel",e."ApiTokenId"
                FROM "ProjectorCatalogEvent" e LEFT JOIN "UserAccount" u ON u."Id"=e."ActorId"
                WHERE e."PageId"=:page ORDER BY e."Id" DESC LIMIT 50
                """).SetParameter("page", page.Id).ListAsync<object[]>(ct)
            : [];
        // A manager learns how the page was opened; the token's name only when it is theirs or they are a wiki administrator.
        var tokens = await ApiTokenVisibility.VisibleAsync(session, await currentActor.RequireAsync(ct),
            visible.Select(row => row[9] as Guid?).Concat(marks.Select(row => row[6] as Guid?)), ct);
        var items = visible.Select(row => new PageAccessEvent(
            Convert.ToInt64(row[0]).ToString(CultureInfo.InvariantCulture), (string)row[1], (DateTime)row[2],
            row[3] as Guid?, row[4] as string, row[5] as Guid?, row[6] as string, (int)row[7])
        {
            Channel = row[8] as string,
            ApiToken = tokens.Find(row[9] as Guid?)
        }).ToArray();
        return new(items, rows.Count > query.Limit ? items[^1].Id : null)
        {
            CatalogMarks = marks.Count == 0 ? null : marks.Select(row => new PageCatalogMark(Convert.ToInt64(row[0]).ToString(CultureInfo.InvariantCulture),
                (bool)row[1], (DateTime)row[2], (Guid)row[3], row[4] as string, (string)row[5]) { ApiToken = tokens.Find(row[6] as Guid?) }).ToArray()
        };
    }
}
