using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PageOrganization.Domain;

namespace Atlas.Server.Features.PageOrganization.Persistence;

/// <remarks>Subselect projections do not participate in NHibernate auto-flush; Synchronize("Page", "PageRevision")
/// only makes a pending Page/PageRevision flush before this projection is queried in the same transaction.
/// Always query it filtered (e.g. by SpaceId), never unfiltered.</remarks>
public sealed class PageTreeEntryMap : ClassMapping<PageTreeEntry>
{
    public PageTreeEntryMap()
    {
        Mutable(false);
        // One row per page: every page has exactly one revision with ActiveTo IS NULL.
        Subselect("""
            SELECT p."Id", p."SpaceId", p."ParentId", p."OwnerId", p."Version", p."SortOrder",
                   p."IsTrashed", p."IsTemplate", p."TrashedAt", p."TrashedById", p."TrashBatchId", p."PurgedAt" IS NOT NULL AS "IsPurged",
                   p."PublishedRevisionId" IS NOT NULL AS "IsPublished", p."SortChildrenByTitle",
                   working."Title" AS "WorkingTitle",
                   published."Title" AS "PublishedTitle"
            FROM "Page" p
            JOIN "PageRevision" working ON working."PageId" = p."Id" AND working."ActiveTo" IS NULL
            LEFT JOIN "PageRevision" published ON published."Id" = p."PublishedRevisionId"
            """);
        Synchronize("Page", "PageRevision");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.SpaceId);
        Property(x => x.ParentId);
        Property(x => x.OwnerId);
        Property(x => x.Version);
        Property(x => x.SortOrder);
        Property(x => x.IsTrashed);
        Property(x => x.IsTemplate);
        Property(x => x.TrashedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.TrashedById);
        Property(x => x.TrashBatchId);
        Property(x => x.IsPurged);
        Property(x => x.IsPublished);
        Property(x => x.WorkingTitle);
        Property(x => x.PublishedTitle);
        Property(x => x.SortChildrenByTitle);
    }
}
