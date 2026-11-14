using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Security.Domain;

namespace Atlas.Server.Features.Security.Persistence;

public sealed class TrustedAddressMap : ClassMapping<TrustedAddress>
{
    public TrustedAddressMap()
    {
        Table("`TrustedAddress`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Purpose);
        Property(x => x.Name);
        Property(x => x.Kind);
        Property(x => x.Range);
        Property(x => x.IsActive);
        Property(x => x.ArchivedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
