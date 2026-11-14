namespace Atlas.Server.Features.Mcp.Domain;

/// <summary>The wiki-level switch of the MCP server module (migration 2026100620). One current row; no row means off (plan O2).
/// History is guarded: only ActiveTo of the current row may be set. Every new current row is audited as mcp.enabled or mcp.disabled.</summary>
public class McpSetting
{
    public virtual Guid Id { get; set; }
    public virtual bool Enabled { get; set; }
    public virtual Guid ChangedById { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
