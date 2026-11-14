namespace Atlas.Server.Features.Attachments.Contracts;

/// <summary>Wire shape returned after an upload and embedded in rich content: Url is the canonical inline/content link (images) or the download link; DownloadUrl always forces a download.</summary>
public sealed record AttachmentDto(Guid Id, string Name, long Size, string MimeType, string Url, string DownloadUrl);
