using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Security.Domain;

namespace Atlas.Server.Features.Security.Persistence;

public sealed class SecurityConfigurationMap : ClassMapping<SecurityConfiguration>
{
    public SecurityConfigurationMap()
    {
        Table("`SecurityConfiguration`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Version);
        Property(x => x.ProxyHopLimit);
        Property(x => x.ProxiesImportedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
