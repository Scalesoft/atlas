namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>atlas_list_attachments: the attachments of a page the actor may open, as the REST list answers them.</summary>
public sealed record ListMcpAttachments(Guid PageId);
