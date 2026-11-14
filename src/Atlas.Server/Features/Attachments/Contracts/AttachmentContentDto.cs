namespace Atlas.Server.Features.Attachments.Contracts;

/// <summary>Metadata needed to serve the blob. The endpoint opens the file; a handler must not hand an open stream across a transaction boundary.</summary>
public sealed record AttachmentContentDto(Guid Id, string Name, string MimeType, string Sha256, DateTime ActiveFrom, DateTime? AuthorityUntil = null);
