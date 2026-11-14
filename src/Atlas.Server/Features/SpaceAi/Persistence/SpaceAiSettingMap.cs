using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.SpaceAi.Domain;

namespace Atlas.Server.Features.SpaceAi.Persistence;

public sealed class SpaceAiSettingMap : ClassMapping<SpaceAiSetting>
{
    public SpaceAiSettingMap()
    {
        Table("`SpaceAiSetting`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.SpaceId);
        Property(x => x.Included);
        Property(x => x.ChangedById);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
