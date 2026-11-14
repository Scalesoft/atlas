using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards;

/// <summary>
/// The boards' relations (spec §13.2): <c>placement</c>, an object shown by live page or object cards of a live board (one edge per
/// object and board with the number of cards, and the section when every card shows the same one; a card of a trashed or missing
/// object is broken), and <c>attached</c>, a page's own board (Purpose) belonging to its page.
/// </summary>
public sealed class BoardRelations : IObjectRelationSource
{
    /// <summary>After <see cref="RelationAnchor.Cte"/>; public for the measured tests (ContentsBudgetTests).</summary>
    public const string Sql = $"""
        , card AS (
          SELECT i."Id",i."BoardId",i."TargetResourceId",i."PropsJson"->>'anchor' AS anchor
          FROM anchor a JOIN "BoardItem" i ON i."BoardId"=a."Id" AND NOT i."IsDeleted" AND i."TargetResourceId" IS NOT NULL
          UNION
          SELECT i."Id",i."BoardId",i."TargetResourceId",i."PropsJson"->>'anchor'
          FROM anchor a JOIN "BoardItem" i ON i."TargetResourceId"=a."Id" AND NOT i."IsDeleted" AND i."TargetResourceId" IS NOT NULL
        ), attached AS (
          SELECT b."Id",b."ParentPageId" FROM anchor a JOIN "Board" b ON b."Id"=a."Id" WHERE b."Purpose" IS NOT NULL AND NOT b."IsTrashed"
          UNION
          SELECT b."Id",b."ParentPageId" FROM anchor a JOIN "Board" b ON b."ParentPageId"=a."Id" WHERE b."Purpose" IS NOT NULL AND NOT b."IsTrashed"
        )
        SELECT c."TargetResourceId",c."BoardId",'{RelationKinds.Placement}',CAST(count(*) AS int),
          CASE WHEN count(c.anchor)=count(*) AND count(DISTINCT c.anchor)=1 THEN min(c.anchor) END
        FROM card c JOIN "Board" b ON b."Id"=c."BoardId" AND NOT b."IsTrashed"
        GROUP BY c."TargetResourceId",c."BoardId"
        UNION ALL
        SELECT "Id","ParentPageId",'{RelationKinds.Attached}',1,NULL FROM attached
        """;

    public RelationStatement Statement { get; } = new(Sql, 
        row => new ObjectEdge(row.GetGuid(0), row.GetGuid(1), row.GetString(2), row.GetInt32(3), row.IsDBNull(4) ? null : row.GetString(4)));
}
