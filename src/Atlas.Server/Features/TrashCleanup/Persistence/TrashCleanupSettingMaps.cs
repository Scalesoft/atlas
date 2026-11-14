using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.TrashCleanup.Domain;

namespace Atlas.Server.Features.TrashCleanup.Persistence;

public sealed class TrashCleanupSettingMap : ClassMapping<TrashCleanupSetting>
{
    public TrashCleanupSettingMap()
    {
        Table("`TrashCleanupSetting`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Scope);
        Property(x => x.Automatic);
        Property(x => x.Days);
        Property(x => x.ChangedById);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}

public sealed class SpaceTrashCleanupSettingMap : ClassMapping<SpaceTrashCleanupSetting>
{
    public SpaceTrashCleanupSettingMap()
    {
        Table("`SpaceTrashCleanupSetting`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.SpaceId);
        Property(x => x.Automatic);
        Property(x => x.Days);
        Property(x => x.ChangedById);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
