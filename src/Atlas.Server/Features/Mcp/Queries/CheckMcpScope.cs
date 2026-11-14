namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>G5 after a read through a shared handler that does not know MCP (history, connections).</summary>
public sealed record CheckMcpScope(IReadOnlyList<Guid> PageIds, IReadOnlyList<Guid> SpaceIds);
