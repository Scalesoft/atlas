namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>atlas_get_attachment and atlas_download_attachment: one attachment under the decision of the download and G5.</summary>
public sealed record GetMcpAttachment(Guid AttachmentId);
