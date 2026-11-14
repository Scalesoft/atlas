using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Domain;
using ISession = NHibernate.ISession;
using Atlas.Server.Features.Auth.OpenIdConnect;

namespace Atlas.Server.Features.ModuleSettings.Queries;

internal static class OidcSettingsReader
{
    public static async Task<StoredOidcSettings?> ReadAsync(ISession session, IDataProtector protector, CancellationToken ct)
    {
        var revision = await session.QueryOver<OidcSettingsRevision>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync(ct);
        if (revision is null)
        {
            return null;
        }
        var providers = await session.QueryOver<OidcProviderSettings>()
            .Where(x => x.SettingsId == revision.Id)
            .OrderBy(x => x.ProviderId).Asc
            .ListAsync(ct);
        var scopes = await session.QueryOver<OidcProviderScope>()
            .Where(x => x.SettingsId == revision.Id)
            .OrderBy(x => x.Scope).Asc
            .ListAsync(ct);
        var mappings = await session.QueryOver<OidcAdminMappingRow>().Where(x => x.SettingsId == revision.Id).ListAsync(ct);
        return new StoredOidcSettings(revision.Id, revision.PublicOrigin, providers.Select(provider => new AtlasOidcProvider
        {
            Id = provider.ProviderId,
            DisplayName = provider.DisplayName,
            Issuer = provider.Issuer,
            ClientId = provider.ClientId,
            ClientSecret = provider.ProtectedSecret is { } secret ? protector.Unprotect(secret) : null,
            AllowSignup = provider.AllowSignup,
            AdminMappings = mappings.Where(x => x.ProviderId == provider.ProviderId).OrderBy(x => x.ClaimType, StringComparer.Ordinal)
                .ThenBy(x => x.ClaimValue, StringComparer.Ordinal).Select(x => new OidcAdminMapping(x.ClaimType, x.ClaimValue)).ToArray(),
            Scopes = scopes.Where(x => x.ProviderId == provider.ProviderId).Select(x => x.Scope).ToArray()
        }).ToArray());
    }

    public static async Task<OidcSettingsView> ToViewAsync(ISession session, StoredOidcSettings settings, OidcRuntimeConfiguration runtime, CancellationToken ct)
    {
        // What removing a provider costs (UX-N-12): the accounts that sign in through its issuer.
        var linked = (await session.CreateSQLQuery("""
                SELECT "Issuer", CAST(COUNT(DISTINCT "UserId") AS integer) FROM "ExternalIdentity" WHERE "ActiveTo" IS NULL GROUP BY "Issuer"
                """).ListAsync<object[]>(ct))
            .GroupBy(row => ((string)row[0]).TrimEnd('/'), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Sum(row => (int)row[1]));
        return new OidcSettingsView(settings.Version, settings.PublicOrigin,
            settings.Providers.Select(p => new OidcProviderView(p.Id, p.DisplayName, p.Issuer, p.ClientId,
                !string.IsNullOrEmpty(p.ClientSecret), p.Scopes, p.AllowSignup,
                string.IsNullOrWhiteSpace(settings.PublicOrigin) ? "" : settings.PublicOrigin.TrimEnd('/') + "/signin-oidc/" + p.Id, p.AdminMappings,
                linked.GetValueOrDefault(p.Issuer.TrimEnd('/')))).ToArray(),
            settings.Version != runtime.Version, runtime.Options.BuiltinEnabled);
    }
}
