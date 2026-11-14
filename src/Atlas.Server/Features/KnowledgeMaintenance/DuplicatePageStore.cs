using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.KnowledgeMaintenance;

/// <summary>G3 reads only current publications in the active generation. Authorization precedes grouping and LIMIT.</summary>
public sealed class DuplicatePageStore(ISession session, TimeProvider clock)
{
    public async Task<IReadOnlyList<DuplicatePagePair>> ListAsync(UserAccount actor, Guid spaceId, CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery($$"""
            WITH {{AccessSql.ActorCte}}, visible AS MATERIALIZED (
              SELECT p."Id",r."Title",k."GenerationId"
              FROM "Page" p JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId"
              JOIN "KnowledgePage" k ON k."PageId"=p."Id" AND k."RevisionId"=p."PublishedRevisionId"
              JOIN "IndexGeneration" g ON g."Id"=k."GenerationId" AND g."State"='Active'
              CROSS JOIN actor a
              WHERE p."SpaceId"=:space AND {{AccessSql.SpaceInAi(":space")}} AND NOT p."IsTrashed" AND NOT p."IsTemplate"
                AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
                  WHERE e."UserId"=a."Id" AND e."PageId"=p."Id" AND e."Role">=1))
            ), pairs AS (
              SELECT LEAST(n."PageId",n."NeighborPageId") AS first,
                     GREATEST(n."PageId",n."NeighborPageId") AS second, max(n."Similarity") AS similarity
              FROM "PageNeighbor" n
              JOIN visible a ON a."Id"=n."PageId" AND a."GenerationId"=n."GenerationId"
              JOIN visible b ON b."Id"=n."NeighborPageId" AND b."GenerationId"=n."GenerationId"
              WHERE n."Similarity">=0.95 AND n."PageId"<>n."NeighborPageId"
              GROUP BY LEAST(n."PageId",n."NeighborPageId"),GREATEST(n."PageId",n."NeighborPageId")
            )
            SELECT p.first,a."Title" AS title,p.second,b."Title" AS other_title,p.similarity
            FROM pairs p JOIN visible a ON a."Id"=p.first JOIN visible b ON b."Id"=p.second
            WHERE NOT EXISTS (SELECT 1 FROM "AiDuplicateDismissal" d WHERE d."PageId"=p.first AND d."OtherPageId"=p.second)
            ORDER BY p.similarity DESC,p.first,p.second LIMIT 20
            """)
            .AddScalar("first", NHibernateUtil.Guid).AddScalar("title", NHibernateUtil.String)
            .AddScalar("second", NHibernateUtil.Guid).AddScalar("other_title", NHibernateUtil.String)
            .AddScalar("similarity", NHibernateUtil.Double)
            .SetParameter("actor", actor.Id).SetParameter("stamp", actor.SecurityStamp).SetParameter("space", spaceId)
            .ListAsync<object[]>(ct);
        return rows.Select(row => new DuplicatePagePair((Guid)row[0], (string)row[1], (Guid)row[2], (string)row[3], (double)row[4])).ToArray();
    }

    public Task DismissAsync(Guid first, Guid second, CancellationToken ct)
        => session.CreateSQLQuery("""
            INSERT INTO "AiDuplicateDismissal" ("PageId","OtherPageId","CreatedAt")
            VALUES (LEAST(CAST(:first AS uuid),CAST(:second AS uuid)),GREATEST(CAST(:first AS uuid),CAST(:second AS uuid)),:now)
            ON CONFLICT ("PageId","OtherPageId") DO NOTHING
            """).SetParameter("first", first).SetParameter("second", second)
            .SetParameter("now", clock.GetUtcNow().UtcDateTime, NHibernateUtil.UtcDateTime).ExecuteUpdateAsync(ct);
}
