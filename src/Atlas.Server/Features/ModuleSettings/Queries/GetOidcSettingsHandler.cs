using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ModuleSettings.Queries;

public sealed class GetOidcSettingsHandler
{
    private readonly ISession m_session;
    private readonly ModuleSettingsAccess m_access;
    private readonly OidcRuntimeConfiguration m_runtime;
    private readonly IDataProtector m_protector;

    public GetOidcSettingsHandler(ISession session, ModuleSettingsAccess access, OidcRuntimeConfiguration runtime, IDataProtectionProvider protection)
    {
        m_session = session;
        m_access = access;
        m_runtime = runtime;
        m_protector = protection.CreateProtector(OidcProtection.Purpose);
    }

    public async Task<OidcSettingsView> Handle(GetOidcSettings query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        var stored = await OidcSettingsReader.ReadAsync(m_session, m_protector, ct);
        return await OidcSettingsReader.ToViewAsync(m_session, stored ?? new StoredOidcSettings(null, m_runtime.Options.PublicOrigin, m_runtime.Options.Providers), m_runtime, ct);
    }
}
