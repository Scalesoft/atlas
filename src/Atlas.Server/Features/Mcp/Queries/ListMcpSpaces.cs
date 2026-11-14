namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>atlas_list_spaces: the spaces of ListApiSpaces without those switched out of AI and MCP (G5).</summary>
public sealed record ListMcpSpaces(int Offset, int Limit);
