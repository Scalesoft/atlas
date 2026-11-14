namespace Atlas.Server.Features.Attachments.Domain;

/// <summary>Immutable membership of an attachment in one saved revision. Composite key (RevisionId, AttachmentId); PageId carries both composite foreign keys.</summary>
public class PageRevisionAttachment
{
    public virtual Guid PageId { get; set; }

    public virtual Guid RevisionId { get; set; }

    public virtual Guid AttachmentId { get; set; }

    public override bool Equals(object? obj)
    {
        return obj is PageRevisionAttachment other && other.RevisionId == RevisionId && other.AttachmentId == AttachmentId;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(RevisionId, AttachmentId);
    }
}
