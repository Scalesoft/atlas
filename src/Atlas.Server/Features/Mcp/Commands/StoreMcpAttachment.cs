namespace Atlas.Server.Features.Mcp.Commands;

/// <summary>atlas_upload_attachment: the metadata of a blob already written under <paramref name="Id"/>, through the shared store.</summary>
public sealed record StoreMcpAttachment(Guid Id, Guid PageId, string Name, long Size, string Sha256, string MimeType, bool IsImage);
