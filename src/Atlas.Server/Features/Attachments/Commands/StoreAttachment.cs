namespace Atlas.Server.Features.Attachments.Commands;

public sealed record StoreAttachment(Guid Id, Guid PageId, string Name, long Size, string Sha256, string MimeType, bool IsImage);
