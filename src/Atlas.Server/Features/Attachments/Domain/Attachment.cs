namespace Atlas.Server.Features.Attachments.Domain;

/// <summary>Binary attached to exactly one page. Removal closes ActiveTo; the blob is never deleted while a revision can reference it.</summary>
public class Attachment
{
    public virtual Guid Id { get; set; }

    public virtual Guid PageId { get; set; }

    public virtual Guid UploadedBy { get; set; }

    public virtual string Name { get; set; } = "";

    public virtual string MimeType { get; set; } = "application/octet-stream";

    public virtual long Size { get; set; }

    public virtual string Sha256 { get; set; } = "";

    public virtual bool IsImage { get; set; }

    public virtual DateTime ActiveFrom { get; set; }

    public virtual DateTime? ActiveTo { get; set; }
}
