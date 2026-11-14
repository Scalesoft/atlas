using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageLinks;

/// <summary>Relation <c>link</c> (spec §13.2): the current publication of a live, non-template page links to a page. A link to a
/// trashed or missing page is returned as well (broken).</summary>
public sealed class PageLinkRelations : IObjectRelationSource
{
    /// <summary>After <see cref="RelationAnchor.Cte"/>; public for the measured tests (ContentsBudgetTests).</summary>
    public const string Sql = """
        , link AS (
          SELECT l.* FROM anchor a JOIN "PageLink" l ON l."SourcePageId"=a."Id"
          UNION
          SELECT l.* FROM anchor a JOIN "PageLink" l ON l."TargetPageId"=a."Id"
        )
        SELECT l."SourcePageId",l."TargetPageId" FROM link l
        JOIN "Page" s ON s."Id"=l."SourcePageId" AND s."PublishedRevisionId"=l."SourceRevisionId"
        WHERE NOT s."IsTrashed" AND NOT s."IsTemplate"
        """;

    public RelationStatement Statement { get; } = new(Sql, row => new ObjectEdge(row.GetGuid(0), row.GetGuid(1), RelationKinds.Link));
}
