using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.AccessAudit.Domain;

namespace Atlas.Server.Features.AccessAudit.Persistence;

public sealed class AccessAuditCoverageMap : ClassMapping<AccessAuditCoverage>
{
    public AccessAuditCoverageMap()
    {
        Table("`SchemaVersion`");
        Mutable(false);
        Id(x => x.Version, m =>
        {
            m.Column("`Version`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.AppliedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
