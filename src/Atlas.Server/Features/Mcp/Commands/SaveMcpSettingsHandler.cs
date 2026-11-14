using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Domain;
using Atlas.Server.Features.Mcp.Queries;
using Atlas.Server.Features.ModuleSettings;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Commands;

/// <summary>Switches the MCP module (WikiAdmin, CAS on the current interval). The caller invalidates <see cref="McpModuleState"/> after
/// the commit; tokens with the channel mcp stop or start working with the next request.</summary>
public sealed class SaveMcpSettingsHandler
{
    private const long SettingsLockKey = 731145027;
    private readonly ISession m_session;
    private readonly ModuleSettingsAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly McpAddress m_address;

    public SaveMcpSettingsHandler(ISession session, ModuleSettingsAccess access, IAtlasLocks locks, McpAddress address)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_address = address;
    }

    public async Task<McpSettingsView> Handle(SaveMcpSettings command, CancellationToken ct)
    {
        var actor = await m_access.RequireWikiAdminAsync(ct);
        await m_locks.AcquireAdvisoryAsync(SettingsLockKey, ct);
        var previous = await CurrentMcp.LoadAsync(m_session, ct);
        AdministrationVersion.Expect(previous?.ActiveFrom, command.Request.ExpectedVersion);
        if (previous is not null && previous.Enabled == command.Request.Enabled)
        {
            // Nothing changes: no new interval, so the audit history records only real decisions.
            return CurrentMcp.ToView(previous, m_address.EndpointUrl());
        }
        var now = Temporal.NextBoundary(previous?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (previous is not null)
        {
            previous.ActiveTo = now;
            // One current row: close it before the replacement insert reaches the database.
            await m_session.FlushAsync(ct);
        }
        var saved = new McpSetting { Id = Guid.NewGuid(), Enabled = command.Request.Enabled, ChangedById = actor.Id, ActiveFrom = now };
        await m_session.SaveAsync(saved, ct);
        await AccessAuditLog.RecordAsync(m_session, actor.Id, saved.Enabled ? "mcp.enabled" : "mcp.disabled", AccessAuditLog.McpSetting, saved.Id, ct);
        return CurrentMcp.ToView(saved, m_address.EndpointUrl());
    }
}
