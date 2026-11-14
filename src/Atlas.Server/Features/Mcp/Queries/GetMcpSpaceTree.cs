namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>atlas_get_space_tree: a window of the whole page tree of one space in depth-first order, after G5.</summary>
public sealed record GetMcpSpaceTree(Guid SpaceId, int Offset, int Limit);
