using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.ModuleSettings.Domain;

namespace Atlas.Server.Features.ModuleSettings.Persistence;

public sealed class OidcAdminMappingRowMap : ClassMapping<OidcAdminMappingRow>
{
    public OidcAdminMappingRowMap()
    {
        Table("`OidcAdminMapping`");
        Mutable(false);
        ComposedId(m =>
        {
            m.Property(x => x.SettingsId, p => p.Column("`SettingsId`"));
            m.Property(x => x.ProviderId, p => p.Column("`ProviderId`"));
            m.Property(x => x.ClaimType, p => p.Column("`ClaimType`"));
            m.Property(x => x.ClaimValue, p => p.Column("`ClaimValue`"));
        });
    }
}
