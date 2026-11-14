namespace Atlas.Server.Features.Attachments.Contracts;

/// <summary>
/// What the attachment list and the metadata of one attachment answer. Url and DownloadUrl are the canonical relative links stored in
/// rich content (they need a browser session); ApiDownloadUrl is the bearer-capable download of the personal API, set only on /api/v1.
/// UsedInPublished and UsedInWorking tell an upload from an attachment a revision actually references; UsedInWorking is null for an
/// actor who may not see the working revision.
/// </summary>
public sealed record AttachmentInfoDto(Guid Id, Guid PageId, string Name, string MimeType, long Size, bool IsImage, string Sha256,
    DateTime UploadedAt, string Url, string DownloadUrl, bool UsedInPublished, bool? UsedInWorking)
{
    public string? ApiDownloadUrl { get; init; }
}

/// <summary>The attachments of one page the actor may open, oldest upload first.</summary>
public sealed record PageAttachmentsDto(Guid PageId, IReadOnlyList<AttachmentInfoDto> Items);
