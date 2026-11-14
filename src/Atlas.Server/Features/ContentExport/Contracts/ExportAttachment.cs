namespace Atlas.Server.Features.ContentExport.Contracts;

public sealed record ExportAttachment(Guid Id, Guid PageId, string Name, string MimeType, long Size, string Sha256, string CanonicalUrl, string ArchivePath);
