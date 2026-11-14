using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Platform.Identity.Persistence;

public sealed class ResourceGrantMap : ClassMapping<ResourceGrant>
{
    public ResourceGrantMap()
    {
        Table("`ResourceGrant`");
        // Closing an interval writes "ActiveTo" alone: the runtime database role may update nothing else (RuntimeDatabaseRole).
        DynamicUpdate(true);
        Mutable(false);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.PageId);
        Property(x => x.ObjectId);
        Property(x => x.VaultId);
        Property(x => x.SecretId);
        Property(x => x.VaultSpaceId);
        Property(x => x.UserId);
        Property(x => x.GroupId);
        Property(x => x.Role);
        Property(x => x.StartAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ExpiresAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
