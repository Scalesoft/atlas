using Atlas.Server.Features.Attachments.Contracts;

namespace Atlas.Server.Features.Mcp.Contracts;

/// <summary>atlas_upload_attachment stored: the shared upload answer and the page's space at the moment of the store (for the audit).</summary>
public sealed record McpStoredAttachment(AttachmentDto Attachment, Guid PageId, Guid SpaceId);
