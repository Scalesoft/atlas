using FluentMigrator;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.ExternalSharing.Domain;

namespace Atlas.Server.Features.ExternalSharing.Persistence;

[Migration(2026091301)]
public sealed class ExternalSharingMigration() : EmbeddedSchemaMigration("2026091301_external_sharing.sql");

[Migration(2026092504)]
public sealed class SharingGrantValidityMigration() : EmbeddedSchemaMigration("2026092504_sharing_grant_validity.sql");

[Migration(2026092622, "Page-level share authority in share link invalidation")]
public sealed class PageShareAuthorityMigration() : EmbeddedSchemaMigration("2026092622_page_share_authority.sql");

public sealed class SpaceExternalSharingPolicyMap : ClassMapping<SpaceExternalSharingPolicy>
{
    public SpaceExternalSharingPolicyMap()
    {
        Table("`SpaceExternalSharingPolicy`");
        Id(x => x.Id, m => { m.Column("`Id`"); m.Generator(Generators.Assigned); });
        Property(x => x.SpaceId, m => m.Update(false));
        Property(x => x.Enabled, m => m.Update(false));
        Property(x => x.ChangedById, m => m.Update(false));
        Property(x => x.ActiveFrom, m => { m.Type(NHibernateUtil.UtcDateTime); m.Update(false); });
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}

public sealed class PageShareLinkMap : ClassMapping<PageShareLink>
{
    public PageShareLinkMap()
    {
        Table("`PageShareLink`");
        Id(x => x.Id, m => { m.Column("`Id`"); m.Generator(Generators.Assigned); });
        Property(x => x.SpaceId, m => m.Update(false));
        Property(x => x.PageId, m => m.Update(false));
        Property(x => x.RevisionId, m => m.Update(false));
        Property(x => x.PolicyId, m => m.Update(false));
        Property(x => x.CreatedById, m => m.Update(false));
        Property(x => x.CreatorSecurityStamp, m => m.Update(false));
        Property(x => x.Name, m => m.Update(false));
        Property(x => x.TokenHash, m => m.Update(false));
        Property(x => x.FollowsLive, m => m.Update(false));
        Property(x => x.ExpiresAt, m => { m.Type(NHibernateUtil.UtcDateTime); m.Update(false); });
        Property(x => x.ActiveFrom, m => { m.Type(NHibernateUtil.UtcDateTime); m.Update(false); });
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
