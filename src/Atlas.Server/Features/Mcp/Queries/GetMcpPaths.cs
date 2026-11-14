using Atlas.Server.Features.Mcp.Contracts;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>Paths of pages a shared handler already returned to the actor (search hits): their titles come from that answer.</summary>
public sealed record GetMcpPaths(IReadOnlyList<McpPathRequest> Pages);
