using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Security.Domain;

namespace Atlas.Server.Features.Security.Persistence;

public sealed class ProtectionSettingMap : ClassMapping<ProtectionSetting>
{
    public ProtectionSettingMap()
    {
        Table("`ProtectionSetting`");
        // Closing an interval writes "ActiveTo" alone: the runtime database role may update nothing else (RuntimeDatabaseRole).
        DynamicUpdate(true);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.VaultId);
        Property(x => x.SecretId);
        Property(x => x.Mode);
        Property(x => x.ConfigurationVersion);
        Property(x => x.ChangedBy);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
