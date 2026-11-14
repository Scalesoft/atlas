using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Auth.OpenIdConnect.Domain;

namespace Atlas.Server.Features.Auth.OpenIdConnect.Persistence;

public sealed class ExternalIdentityMap : ClassMapping<ExternalIdentity>
{
    public ExternalIdentityMap()
    {
        Table("`ExternalIdentity`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.UserId);
        Property(x => x.Issuer);
        Property(x => x.Subject);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
