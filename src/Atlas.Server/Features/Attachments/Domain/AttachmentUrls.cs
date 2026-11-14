using Atlas.Server.Features.Attachments.Contracts;

namespace Atlas.Server.Features.Attachments.Domain;

/// <summary>Canonical attachment URLs. Images get an inline content link; everything else downloads.</summary>
public static class AttachmentUrls
{
    public static AttachmentDto Describe(Attachment attachment)
    {
        return new AttachmentDto(attachment.Id, attachment.Name, attachment.Size, attachment.MimeType,
            $"/api/attachments/{attachment.Id:D}/{(attachment.IsImage ? "content" : "download")}",
            $"/api/attachments/{attachment.Id:D}/download");
    }
}
