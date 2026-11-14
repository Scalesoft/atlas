namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>atlas_list_pages: G5 for the space, then the editor-only facts of the given pages the actor may edit.</summary>
public sealed record GetMcpPageStates(Guid SpaceId, IReadOnlyList<Guid> PageIds);
