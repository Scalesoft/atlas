using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.ModuleSettings.Domain;

namespace Atlas.Server.Features.ModuleSettings.Persistence;

public sealed class OidcProviderScopeMap : ClassMapping<OidcProviderScope>
{
    public OidcProviderScopeMap()
    {
        Table("`OidcProviderScope`");
        // Insert only: the whole triple is the key, so there is nothing an update could change.
        Mutable(false);
        ComposedId(m =>
        {
            m.Property(x => x.SettingsId, p => p.Column("`SettingsId`"));
            m.Property(x => x.ProviderId, p => p.Column("`ProviderId`"));
            m.Property(x => x.Scope, p => p.Column("`Scope`"));
        });
    }
}
