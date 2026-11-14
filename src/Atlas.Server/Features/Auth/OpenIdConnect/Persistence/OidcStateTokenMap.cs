using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Auth.OpenIdConnect.Domain;

namespace Atlas.Server.Features.Auth.OpenIdConnect.Persistence;

/// <summary>The Version column makes state redemption atomic across requests and processes.</summary>
public sealed class OidcStateTokenMap : ClassMapping<OidcStateToken>
{
    public OidcStateTokenMap()
    {
        Table("`OidcStateToken`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Version(x => x.Version, m => m.Column("`Version`"));
        Property(x => x.ApplicationId);
        Property(x => x.AuthorizationId);
        Property(x => x.CreationDate, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ExpirationDate, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.RedemptionDate, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.Payload, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.Properties, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.ReferenceId);
        Property(x => x.Status);
        Property(x => x.Subject);
        Property(x => x.Type);
    }
}
