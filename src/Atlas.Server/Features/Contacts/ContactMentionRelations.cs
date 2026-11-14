using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts;

/// <summary>Relation <c>mention</c> (spec §13.1 point 2): the current publication of a live, non-template page mentions a contact
/// (PageObjectMention, migrations 2026102840 and 2026102860: never another type's object, re-matched when the page changes space). A mentioned contact that is trashed or gone is returned as well (broken); one merged into another (mergedInto, contacts spec §5) is that other one.
/// With drafts also its current working revision (migration 2026103140): a mention only the draft holds is <see cref="ObjectEdge.Draft"/>.</summary>
public sealed class ContactMentionRelations : IObjectRelationSource
{
    /// <summary>After <see cref="RelationAnchor.Cte"/>; public for the measured tests (ContentsBudgetTests). The projection holds rows of
    /// the publication and of the working revision only, so any row that is not the publication's is the draft's. The merged contacts
    /// are few and read by their partial index (migration 2026103190); a merge repoints every redirect to one hop.</summary>
    public const string Sql = """
        , merged AS MATERIALIZED (
          SELECT c."Id",CAST(c."Data"->>'mergedInto' AS uuid) AS "Into" FROM "ContentResource" c
          WHERE c."TypeId"='atlas.contact' AND c."Data"->>'mergedInto' IS NOT NULL
        ), mention AS (
          SELECT m.* FROM anchor a JOIN "PageObjectMention" m ON m."SourcePageId"=a."Id"
          UNION
          SELECT m.* FROM anchor a JOIN "PageObjectMention" m ON m."ObjectId"=a."Id"
          UNION
          SELECT m.* FROM anchor a JOIN merged g ON g."Into"=a."Id" JOIN "PageObjectMention" m ON m."ObjectId"=g."Id"
        )
        SELECT m."SourcePageId",COALESCE(g."Into",m."ObjectId"),bool_and(m."SourceRevisionId" IS DISTINCT FROM s."PublishedRevisionId") FROM mention m
        LEFT JOIN merged g ON g."Id"=m."ObjectId"
        JOIN "Page" s ON s."Id"=m."SourcePageId" AND (s."PublishedRevisionId"=m."SourceRevisionId" OR :drafts)
        WHERE NOT s."IsTrashed" AND NOT s."IsTemplate"
        GROUP BY m."SourcePageId",COALESCE(g."Into",m."ObjectId")
        """;

    public RelationStatement Statement { get; } = new(Sql, row => new ObjectEdge(row.GetGuid(0), row.GetGuid(1), RelationKinds.Mention, Draft: row.GetBoolean(2)));
}
