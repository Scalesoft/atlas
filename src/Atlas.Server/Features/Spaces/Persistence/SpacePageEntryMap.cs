using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Spaces.Domain;

namespace Atlas.Server.Features.Spaces.Persistence;

/// <remarks>
/// No auto-flush: a Subselect has its own query space and mapping by code has no Synchronize(...), so pending
/// inserts into "Page" or "PageRevision" are invisible here. Call FlushAsync before reading this projection in a
/// session that has pending writes to the underlying tables.
/// </remarks>
public sealed class SpacePageEntryMap : ClassMapping<SpacePageEntry>
{
    public SpacePageEntryMap()
    {
        Mutable(false);
        // Column aliases must match the quoted PascalCase columns produced by AtlasMappings.BeforeMapProperty.
        Subselect("""
            SELECT p."Id", p."SpaceId", p."ParentId", p."OwnerId", p."Version", p."SortOrder", p."IsTrashed", p."IsTemplate",
                p."PublishedRevisionId" IS NOT NULL AS "IsPublished",
                d."Title" AS "DraftTitle",
                b."Title" AS "PublishedTitle",
                d."ActiveFrom" AS "DraftActiveFrom",
                b."ActiveFrom" AS "PublishedActiveFrom"
            FROM "Page" p
            LEFT JOIN "PageRevision" d ON d."PageId" = p."Id" AND d."ActiveTo" IS NULL
            LEFT JOIN "PageRevision" b ON b."PageId" = p."Id" AND b."Id" = p."PublishedRevisionId"
            """);
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
        Property(x => x.IsPublished);
        Property(x => x.DraftTitle);
        Property(x => x.PublishedTitle);
        Property(x => x.DraftActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.PublishedActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
