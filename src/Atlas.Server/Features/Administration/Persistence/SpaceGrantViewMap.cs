using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Administration.Domain;

namespace Atlas.Server.Features.Administration.Persistence;

public sealed class SpaceGrantViewMap : ClassMapping<SpaceGrantView>
{
    public SpaceGrantViewMap()
    {
        Mutable(false);
        Table("`SpaceGrantView`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.SpaceId);
        Property(x => x.UserId);
        Property(x => x.GroupId);
        Property(x => x.Name);
        Property(x => x.Role);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.StartAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ExpiresAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
