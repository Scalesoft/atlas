namespace Atlas.Server.Features.Boards.Domain;

/// <summary>A committed batch, as applied (BoardEditing.Normalize). The composite key is the identifier, so NHibernate needs value equality.</summary>
public class BoardOperation
{
    public virtual Guid BoardId { get; set; }
    public virtual long Sequence { get; set; }
    public virtual Guid OperationId { get; set; }
    public virtual Guid AuthorId { get; set; }
    public virtual DateTime AppliedAt { get; set; }
    public virtual string ChangesJson { get; set; } = "";

    public override bool Equals(object? obj)
    {
        return obj is BoardOperation other && other.BoardId == BoardId && other.Sequence == Sequence;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(BoardId, Sequence);
    }
}
