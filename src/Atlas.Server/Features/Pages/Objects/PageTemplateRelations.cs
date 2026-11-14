using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Objects;

/// <summary>Relation <c>template</c> (plan 3.1 §2.1, U1): a live page was created from a template (Page.TemplatePageId, migration
/// 2026103180), so the template's „Použito na“ lists the pages made from it. A template in the trash or gone is returned as well (broken).
/// Attachments are no use of their own: they belong to one page.</summary>
public sealed class PageTemplateRelations : IObjectRelationSource
{
    /// <summary>After <see cref="RelationAnchor.Cte"/>.</summary>
    public const string Sql = """
        , origin AS (
          SELECT p."Id",p."TemplatePageId" FROM anchor a JOIN "Page" p ON p."Id"=a."Id" WHERE p."TemplatePageId" IS NOT NULL
          UNION
          SELECT p."Id",p."TemplatePageId" FROM anchor a JOIN "Page" p ON p."TemplatePageId"=a."Id"
        )
        SELECT o."Id",o."TemplatePageId" FROM origin o JOIN "Page" s ON s."Id"=o."Id" WHERE NOT s."IsTrashed"
        """;

    public RelationStatement Statement { get; } = new(Sql, row => new ObjectEdge(row.GetGuid(0), row.GetGuid(1), RelationKinds.Template));
}
