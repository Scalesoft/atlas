using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.ModuleSettings;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Queries;

public sealed class GetMcpSettingsHandler
{
    private readonly ISession m_session;
    private readonly ModuleSettingsAccess m_access;
    private readonly McpAddress m_address;

    public GetMcpSettingsHandler(ISession session, ModuleSettingsAccess access, McpAddress address)
    {
        m_session = session;
        m_access = access;
        m_address = address;
    }

    public async Task<McpSettingsView> Handle(GetMcpSettings query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        return CurrentMcp.ToView(await CurrentMcp.LoadAsync(m_session, ct), m_address.EndpointUrl());
    }
}
