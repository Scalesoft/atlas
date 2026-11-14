namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>The preflight of atlas_upload_attachment: the page must be editable and in a space MCP may see before any byte is decoded.</summary>
public sealed record AuthorizeMcpAttachmentUpload(Guid PageId);
