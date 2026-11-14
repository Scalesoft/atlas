using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Security.Domain;

namespace Atlas.Server.Features.Security.Persistence;

public sealed class OidcFreshAuthStatusMap : ClassMapping<OidcFreshAuthStatus>
{
    public OidcFreshAuthStatusMap()
    {
        Table("`OidcFreshAuthStatus`");
        Id(x => x.ProviderId, m =>
        {
            m.Column("`ProviderId`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Issuer);
        Property(x => x.Verdict);
        Property(x => x.AuthTimePresent);
        Property(x => x.AuthTimeFresh);
        Property(x => x.SelfTest);
        Property(x => x.CheckedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.CheckedBy);
    }
}
