using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.ModuleSettings.Domain;

namespace Atlas.Server.Features.ModuleSettings.Persistence;

public sealed class OidcProviderSettingsMap : ClassMapping<OidcProviderSettings>
{
    public OidcProviderSettingsMap()
    {
        Table("`OidcProviderSettings`");
        // Insert only: a revision fans out once and its provider rows are never edited afterwards.
        Mutable(false);
        ComposedId(m =>
        {
            m.Property(x => x.SettingsId, p => p.Column("`SettingsId`"));
            m.Property(x => x.ProviderId, p => p.Column("`ProviderId`"));
        });
        Property(x => x.DisplayName);
        Property(x => x.Issuer);
        Property(x => x.ClientId);
        Property(x => x.ProtectedSecret);
        Property(x => x.AllowSignup);
    }
}
