using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Projector;

/// <summary>
/// The uses requirement blocks make (plan 3.1 M4b, U1), one statement for both kinds, so a usage read pays one round trip for Projector:
/// <list type="bullet">
/// <item><c>party</c>: a requirement of a live, non-template page waits on a contact (ProjectorRequirementIndex.PartyContactId, migration
/// 2026103160), from the page to the contact. A contact merged into another (mergedInto) is that other one, as for a mention (read by
/// the partial index of migration 2026103190, as ContactMentionRelations), also for the uses of the other one; one
/// trashed or gone is returned too (broken).</item>
/// <item><c>catalog</c>: a deployment page uses a catalog requirement, the block of a catalog page of its space with the same key (the
/// definition that counts: the page marked first, then document order, as RequirementResolver.Distinct), from the page to the catalog
/// page with the key as the edge's anchor, so the uses of one catalog requirement are its page's with that part. Only the published
/// catalog pages define.</item>
/// </list>
/// With drafts also the pages' working revisions: a use only a draft holds is <see cref="ObjectEdge.Draft"/>. The index holds the rows
/// whether the module is on or not.
/// </summary>
public sealed class ProjectorRelations : IObjectRelationSource
{
    public const string Sql = """
        , merged AS MATERIALIZED (
          SELECT c."Id",CAST(c."Data"->>'mergedInto' AS uuid) AS "Into" FROM "ContentResource" c
          WHERE c."TypeId"='atlas.contact' AND c."Data"->>'mergedInto' IS NOT NULL
        ), party AS (
          SELECT i."PageId",i."PartyContactId",i."IsPublished" FROM anchor a JOIN "ProjectorRequirementIndex" i ON i."PageId"=a."Id"
          WHERE i."PartyContactId"<>''
          UNION
          SELECT i."PageId",i."PartyContactId",i."IsPublished" FROM anchor a
          JOIN "ProjectorRequirementIndex" i ON i."PartyContactId"=CAST(a."Id" AS text) AND i."PartyContactId"<>''
          UNION
          SELECT i."PageId",i."PartyContactId",i."IsPublished" FROM anchor a JOIN merged g ON g."Into"=a."Id"
          JOIN "ProjectorRequirementIndex" i ON i."PartyContactId"=CAST(g."Id" AS text) AND i."PartyContactId"<>''
        ), linked AS (
          SELECT p."PageId",CAST(p."PartyContactId" AS uuid) AS "ObjectId",p."IsPublished" FROM party p
          WHERE p."PartyContactId" ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
        ), definition AS MATERIALIZED (
          SELECT DISTINCT ON (p."SpaceId",i."Key") i."PageId",p."SpaceId",i."Key"
          FROM "ProjectorCatalogPage" m JOIN "Page" p ON p."Id"=m."PageId" AND NOT p."IsTrashed"
          JOIN "ProjectorRequirementIndex" i ON i."PageId"=m."PageId" AND i."IsPublished" AND i."ParentBlockId" IS NULL AND i."Key"<>''
          WHERE p."SpaceId" IN (SELECT s."SpaceId" FROM anchor a JOIN "Page" s ON s."Id"=a."Id")
          ORDER BY p."SpaceId",i."Key",m."MarkedAt",m."PageId",i."Position"
        ), used AS (
          SELECT d."PageId" AS "CatalogId",i."PageId",i."Key",bool_or(i."IsPublished") AS "Published",count(*) AS "Count"
          FROM definition d JOIN "Page" s ON s."SpaceId"=d."SpaceId" AND s."Id"<>d."PageId"
          JOIN "ProjectorRequirementIndex" i ON i."PageId"=s."Id" AND i."Key"=d."Key" AND (i."IsPublished" OR :drafts)
          WHERE NOT s."IsTrashed" AND NOT s."IsTemplate" AND NOT EXISTS (SELECT 1 FROM "ProjectorCatalogPage" c WHERE c."PageId"=s."Id")
            -- Before the grouping: a page anchor reads its own blocks only, not every page of its space.
            AND (s."Id" IN (SELECT "Id" FROM anchor) OR d."PageId" IN (SELECT "Id" FROM anchor))
          GROUP BY d."PageId",i."PageId",i."Key"
        )
        SELECT 'party',l."PageId",COALESCE(CAST(c."Data"->>'mergedInto' AS uuid),l."ObjectId"),NULL,NOT bool_or(l."IsPublished"),count(*) FROM linked l
        LEFT JOIN "ContentResource" c ON c."Id"=l."ObjectId"
        JOIN "Page" s ON s."Id"=l."PageId" AND (l."IsPublished" OR :drafts)
        WHERE NOT s."IsTrashed" AND NOT s."IsTemplate"
        GROUP BY l."PageId",COALESCE(CAST(c."Data"->>'mergedInto' AS uuid),l."ObjectId")
        UNION ALL
        SELECT 'catalog',u."PageId",u."CatalogId",u."Key",NOT u."Published",u."Count" FROM used u
        """;

    public RelationStatement Statement { get; } = new(Sql, row => new ObjectEdge(row.GetGuid(1), row.GetGuid(2),
        row.GetString(0) == "party" ? RelationKinds.Party : RelationKinds.Catalog, (int)row.GetInt64(5), row.IsDBNull(3) ? null : row.GetString(3),
        row.GetBoolean(4)));
}
