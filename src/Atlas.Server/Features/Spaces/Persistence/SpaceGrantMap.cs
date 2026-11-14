using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Features.Spaces.Persistence;

public sealed class SpaceGrantMap : ClassMapping<SpaceGrant>
{
    public SpaceGrantMap()
    {
        Table("`SpaceGrant`");
        // Closing an interval writes "ActiveTo" alone: the runtime database role may update nothing else (RuntimeDatabaseRole).
        DynamicUpdate(true);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.SpaceId);
        Property(x => x.UserId);
        Property(x => x.GroupId);
        Property(x => x.Role);
        Property(x => x.StartAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ExpiresAt, m => m.Type(NHibernateUtil.UtcDateTime));
        // Wave 4a gave the entity an ActiveFrom initializer and mapped the column, so the map owns it now
        // rather than leaning on the database default.
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
