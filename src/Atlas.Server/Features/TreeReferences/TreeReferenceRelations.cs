using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.TreeReferences;

/// <summary>Relation <c>reference</c> (spec §13.2): a live reference node of the tree places its target under a page, or at the root of
/// the space read as a whole (the space's id as the source, RelationGraph). A target that is trashed or gone is returned as well (broken).</summary>
// shortcut: a node at the root is a use in its space's contents only; "Kde je použito" names no place for it until the web shows a space as one.
public sealed class TreeReferenceRelations : IObjectRelationSource
{
    public const string Sql = """
        , ref AS (
          SELECT t.* FROM anchor a JOIN "TreeReference" t ON t."ParentPageId"=a."Id"
          UNION
          SELECT t.* FROM anchor a JOIN "TreeReference" t ON t."TargetId"=a."Id"
          UNION
          SELECT t.* FROM "TreeReference" t WHERE :whole AND t."SpaceId"=:space AND t."ParentPageId" IS NULL AND NOT t."IsTrashed"
        )
        SELECT coalesce(t."ParentPageId",t."SpaceId"),t."TargetId",count(*)::int,
          CASE WHEN count(DISTINCT t."Anchor")=1 AND count(t."Anchor")=count(*) THEN min(t."Anchor") END
        FROM ref t WHERE NOT t."IsTrashed" AND (t."ParentPageId" IS NOT NULL OR :whole AND t."SpaceId"=:space)
        GROUP BY coalesce(t."ParentPageId",t."SpaceId"),t."TargetId"
        """;

    public RelationStatement Statement { get; } = new(Sql, 
        row => new ObjectEdge(row.GetGuid(0), row.GetGuid(1), RelationKinds.Reference, row.GetInt32(2), row.IsDBNull(3) ? null : row.GetString(3)));
}
