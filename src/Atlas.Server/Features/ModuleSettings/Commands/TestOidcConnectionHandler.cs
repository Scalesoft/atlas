using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Domain;
using Atlas.Server.Features.ModuleSettings.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ModuleSettings.Commands;

public sealed class TestOidcConnectionHandler
{
    private readonly ISession m_session;
    private readonly ModuleSettingsAccess m_access;
    private readonly OidcRuntimeConfiguration m_runtime;
    private readonly IHttpClientFactory m_clients;
    private readonly IDataProtector m_protector;

    public TestOidcConnectionHandler(ISession session, ModuleSettingsAccess access, OidcRuntimeConfiguration runtime,
        IHttpClientFactory clients, IDataProtectionProvider protection)
    {
        m_session = session;
        m_access = access;
        m_runtime = runtime;
        m_clients = clients;
        m_protector = protection.CreateProtector(OidcProtection.Purpose);
    }

    public async Task<OidcConnectionTest> Handle(TestOidcConnection command, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        var stored = await OidcSettingsReader.ReadAsync(m_session, m_protector, ct)
            ?? new StoredOidcSettings(null, m_runtime.Options.PublicOrigin, m_runtime.Options.Providers);
        var provider = stored.Providers.FirstOrDefault(p => p.Id == command.Request.Id)
            ?? throw new ApiException(404, "Poskytovatel nebyl nalezen. Nejdříve nastavení uložte.");
        // The read is finished before the probe starts; the transaction stays open but idle for at most the client timeout.
        return await OidcDiscoveryProbe.ProbeAsync(m_clients.CreateClient("oidc-settings-discovery"), provider.Issuer, ct);
    }
}
