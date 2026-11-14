using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Platform.Identity.Persistence;

/// <remarks>Always query with UserId: the view pushes that filter into every grant branch and its UserId-leading indexes.
/// Synchronize makes pending grant, membership, account and page writes flush before this projection is read.</remarks>
public sealed class PageAccessEntryMap : ClassMapping<PageAccessEntry>
{
    public PageAccessEntryMap()
    {
        Mutable(false);
        Subselect("""
            SELECT a."UserId", a."PageId", a."SpaceId", a."Role", p."IsTrashed",
                p."PublishedRevisionId" IS NOT NULL AS "IsPublished", p."OwnerId"
            FROM "EffectivePageAccess" a
            JOIN "Page" p ON p."Id" = a."PageId"
            """);
        Synchronize("Page", "SpaceGrant", "ResourceGrant", "GroupMember", "UserAccount",
            "ExternalIdentity", "HubIdentityState", "HubConnectionRevision", "HubGroupBinding");
        ComposedId(m =>
        {
            m.Property(x => x.UserId, p => p.Column("`UserId`"));
            m.Property(x => x.PageId, p => p.Column("`PageId`"));
        });
        Property(x => x.SpaceId);
        Property(x => x.Role);
        Property(x => x.IsTrashed);
        Property(x => x.IsPublished);
        Property(x => x.OwnerId);
    }
}
