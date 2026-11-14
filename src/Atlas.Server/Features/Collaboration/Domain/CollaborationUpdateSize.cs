namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>Payload sizes without loading the payload: append and flush bound the pending log by count and bytes.</summary>
public class CollaborationUpdateSize
{
    public virtual Guid PageId { get; set; }
    public virtual Guid Generation { get; set; }
    public virtual long Sequence { get; set; }
    public virtual int Bytes { get; set; }

    public override bool Equals(object? obj)
    {
        return obj is CollaborationUpdateSize other && other.PageId == PageId && other.Generation == Generation && other.Sequence == Sequence;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(PageId, Generation, Sequence);
    }
}
