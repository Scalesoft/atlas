using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Domain;
using Atlas.Server.Features.ModuleSettings.Queries;
using ISession = NHibernate.ISession;
using Atlas.Server.Features.Auth.OpenIdConnect;

namespace Atlas.Server.Features.ModuleSettings.Commands;

public sealed class SaveOidcConfigurationHandler
{
    private const long SettingsLockKey = 731145014;
    private const int MaxProviders = 10;
    private readonly ISession m_session;
    private readonly ModuleSettingsAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly OidcRuntimeConfiguration m_runtime;
    private readonly IDataProtector m_protector;
    private readonly ISecurityChangeGuard m_guard;

    public SaveOidcConfigurationHandler(ISession session, ModuleSettingsAccess access, IAtlasLocks locks,
        OidcRuntimeConfiguration runtime, IDataProtectionProvider protection, ISecurityChangeGuard guard)
    {
        m_session = session;
        m_guard = guard;
        m_access = access;
        m_locks = locks;
        m_runtime = runtime;
        m_protector = protection.CreateProtector(OidcProtection.Purpose);
    }

    public async Task<OidcSettingsView> Handle(SaveOidcConfiguration command, CancellationToken ct)
    {
        var request = command.Request;
        // Authorization first: the legacy endpoint resolved the administrator as an argument, before the save body
        // ever looked at the payload, so a non-administrator sees 403 even with a malformed request.
        var actor = await m_access.RequireWikiAdminAsync(ct);
        if (request.Providers is null || request.Providers.Length > MaxProviders || request.PublicOrigin is null || request.PublicOrigin.Length > 2048)
        {
            throw new ApiException(400, "Neplatné nastavení přihlášení. Lze přidat nejvýše 10 poskytovatelů.");
        }
        await m_locks.AcquireAdvisoryAsync(SettingsLockKey, ct);
        var current = await OidcSettingsReader.ReadAsync(m_session, m_protector, ct);
        if (current?.Version != request.ExpectedVersion)
        {
            throw new ApiException(409, "Nastavení změnil jiný správce. Načtěte je znovu.");
        }
        var previousProviders = current?.Providers ?? m_runtime.Options.Providers;
        var providers = request.Providers.Select(p => Validate(p, previousProviders)).ToArray();
        var options = new AtlasOidcOptions
        {
            BuiltinEnabled = m_runtime.Options.BuiltinEnabled,
            PublicOrigin = request.PublicOrigin.Trim().TrimEnd('/'),
            Providers = providers
        };
        try
        {
            options.Validate();
        }
        catch (InvalidOperationException)
        {
            throw new ApiException(400, "Zkontrolujte HTTPS adresu wiki a issueru, unikátní ID poskytovatele, ID klienta, scopes a pravidla správce (nejvýše 20 unikátních dvojic claim/hodnota). Interní claimy oi_, odhlášení u poskytovatele ani offline_access nejsou podporované.");
        }
        // SEC-A-1: a provider with sign-up and an administrator rule makes new wiki administrators, which by hand needs a fresh
        // sign-in (account.wiki_admin); the saved revision is audited as oidc.configuration_changed.
        await m_guard.RequireAsync(actor, new SecurityChange("oidc.configuration", Guid.Empty), ct);
        var now = DateTime.UtcNow;
        var revision = await m_session.QueryOver<OidcSettingsRevision>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync(ct);
        if (revision is not null)
        {
            now = Temporal.NextBoundary(revision.ActiveFrom, now);
            revision.ActiveTo = now;
            // One current revision per installation: close before the replacement insert reaches the database.
            await m_session.FlushAsync(ct);
        }
        var version = Guid.NewGuid();
        await m_session.SaveAsync(new OidcSettingsRevision
        {
            Id = version,
            PublicOrigin = options.PublicOrigin,
            AuthorId = actor.Id,
            ActiveFrom = now
        }, ct);
        await AccessAuditLog.RecordAsync(m_session, actor.Id, "oidc.configuration_changed", AccessAuditLog.OidcSettingsRevision, version, ct);
        foreach (var provider in providers)
        {
            await m_session.SaveAsync(new OidcProviderSettings
            {
                SettingsId = version,
                ProviderId = provider.Id,
                DisplayName = provider.DisplayName,
                Issuer = provider.Issuer,
                ClientId = provider.ClientId,
                ProtectedSecret = provider.ClientSecret is null ? null : m_protector.Protect(provider.ClientSecret),
                AllowSignup = provider.AllowSignup
            }, ct);
            foreach (var scope in provider.Scopes)
            {
                await m_session.SaveAsync(new OidcProviderScope { SettingsId = version, ProviderId = provider.Id, Scope = scope }, ct);
            }
            foreach (var mapping in provider.AdminMappings)
            {
                await m_session.SaveAsync(new OidcAdminMappingRow { SettingsId = version, ProviderId = provider.Id,
                    ClaimType = mapping.ClaimType, ClaimValue = mapping.ClaimValue }, ct);
            }
        }
        return await OidcSettingsReader.ToViewAsync(m_session, new StoredOidcSettings(version, options.PublicOrigin, providers), m_runtime, ct);
    }

    private AtlasOidcProvider Validate(SaveOidcProvider? p, AtlasOidcProvider[] previousProviders)
    {
        if (p is null || p.Id is null || p.Id.Length > 40 || p.ClientId is null || p.ClientId.Length > 2048 || p.ClientSecret?.Length > 8192
            || p.Issuer is null || p.Issuer.Length > 2048 || p.DisplayName is null || p.DisplayName.Length > 200
            || p.Scopes is null || p.Scopes.Length > 20 || p.Scopes.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200))
        {
            throw new ApiException(400, "Neplatné údaje poskytovatele přihlášení.");
        }
        var previous = previousProviders.FirstOrDefault(x => x.Id == p.Id);
        // Changing issuer/client must never forward a secret saved for another registration.
        var sameRegistration = previous?.Issuer == p.Issuer.Trim() && previous.ClientId == p.ClientId.Trim();
        var priorSecret = sameRegistration ? previous?.ClientSecret : null;
        if (!sameRegistration && previous?.ClientSecret is not null && string.IsNullOrEmpty(p.ClientSecret) && !p.ClearSecret)
        {
            throw new ApiException(400, "Při změně issueru nebo Client ID zadejte nový secret, nebo jej výslovně odeberte.");
        }
        var protectedValue = OidcSecrets.Resolve(p.ClientSecret, p.ClearSecret, priorSecret is null ? null : m_protector.Protect(priorSecret), m_protector);
        return new AtlasOidcProvider
        {
            Id = p.Id,
            DisplayName = p.DisplayName.Trim(),
            Issuer = p.Issuer.Trim(),
            ClientId = p.ClientId.Trim(),
            ClientSecret = protectedValue is null ? null : m_protector.Unprotect(protectedValue),
            Scopes = p.Scopes.Distinct(StringComparer.Ordinal).ToArray(),
            AllowSignup = p.AllowSignup,
            AdminMappings = p.AdminMappings ?? []
        };
    }
}
