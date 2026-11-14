using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>The current row of the MCP module switch. Never stored means off (O2).</summary>
public static class CurrentMcp
{
    public static Task<McpSetting?> LoadAsync(ISession session, CancellationToken ct)
        => session.QueryOver<McpSetting>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync(ct)!;

    public static McpSettingsView ToView(McpSetting? row, string url)
        => new(row?.Enabled ?? false, url, row is null ? null : AdministrationVersion.Format(row.ActiveFrom));
}
