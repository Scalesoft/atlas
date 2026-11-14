using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.PersonalLibrary.Domain;

namespace Atlas.Server.Features.PersonalLibrary.Persistence;

/// <remarks>Subselect projections do not participate in NHibernate auto-flush; Synchronize("Page", "PageRevision",
/// "PagePublication", "Space") makes a pending flush happen before this projection is queried in the same
/// transaction. Always query it filtered (e.g. by Id or SpaceId), never unfiltered.</remarks>
public sealed class LibraryPageEntryMap : ClassMapping<LibraryPageEntry>
{
    public LibraryPageEntryMap()
    {
        Mutable(false);
        Subselect("""
            SELECT p."Id", p."SpaceId", s."Name" AS "SpaceName", p."ParentId", p."OwnerId", p."Version",
                   p."IsTrashed", p."IsTemplate", p."PublishedRevisionId" IS NOT NULL AS "IsPublished",
                   working."Id" AS "WorkingRevisionId", p."PublishedRevisionId",
                   working."Title" AS "WorkingTitle",
                   working."ActiveFrom" AS "WorkingActiveFrom",
                   published."Title" AS "PublishedTitle", published."Version" AS "PublishedVersion",
                   publication."ActiveFrom" AS "PublishedAt", publication."ChangeSummary" AS "PublishedChangeSummary",
                   publication."Channel" AS "PublicationChannel", publication."ApiTokenId" AS "PublicationApiTokenId",
                    parent."IsTrashed" AS "ParentIsTrashed", parent."IsTemplate" AS "ParentIsTemplate",
                   parent."PublishedRevisionId" IS NOT NULL AS "ParentIsPublished",
                   parent."OwnerId" AS "ParentOwnerId"
            FROM "Page" p
            JOIN "Space" s ON s."Id" = p."SpaceId"
            JOIN "PageRevision" working ON working."PageId" = p."Id" AND working."ActiveTo" IS NULL
            LEFT JOIN "PageRevision" published ON published."Id" = p."PublishedRevisionId"
            LEFT JOIN "PagePublication" publication ON publication."PageId" = p."Id" AND publication."ActiveTo" IS NULL
            LEFT JOIN "Page" parent ON parent."Id" = p."ParentId"
            """);
        Synchronize("Page", "PageRevision", "PagePublication", "Space");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.SpaceId);
        Property(x => x.SpaceName);
        Property(x => x.ParentId);
        Property(x => x.OwnerId);
        Property(x => x.Version);
        Property(x => x.IsTrashed);
        Property(x => x.IsTemplate);
        Property(x => x.IsPublished);
        Property(x => x.WorkingRevisionId);
        Property(x => x.PublishedRevisionId);
        Property(x => x.WorkingTitle);
        Property(x => x.WorkingActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.PublishedTitle);
        Property(x => x.PublishedVersion);
        Property(x => x.PublishedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.PublishedChangeSummary);
        Property(x => x.PublicationChannel);
        Property(x => x.PublicationApiTokenId);
        Property(x => x.ParentIsTrashed);
        Property(x => x.ParentIsTemplate);
        Property(x => x.ParentIsPublished);
        Property(x => x.ParentOwnerId);
    }
}
