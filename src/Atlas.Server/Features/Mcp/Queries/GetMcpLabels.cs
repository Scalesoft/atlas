namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>atlas_get_labels: decoration and tags of one page through the shared readers, after G5.</summary>
public sealed record GetMcpLabels(Guid PageId);
