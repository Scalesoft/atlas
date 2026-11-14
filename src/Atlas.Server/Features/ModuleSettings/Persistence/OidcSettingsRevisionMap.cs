using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.ModuleSettings.Domain;

namespace Atlas.Server.Features.ModuleSettings.Persistence;

public sealed class OidcSettingsRevisionMap : ClassMapping<OidcSettingsRevision>
{
    public OidcSettingsRevisionMap()
    {
        Table("`OidcSettingsRevision`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.PublicOrigin);
        Property(x => x.AuthorId);
        Property(x => x.ActiveFrom, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ActiveTo, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
