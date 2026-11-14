namespace Atlas.Server.Features.Objects.Domain;

/// <summary>A confirmed change of an object the platform stores: the version it made and its change id (idempotency), as
/// applied. The composite key is the identifier, so NHibernate needs value equality.</summary>
public class ObjectChange
{
    public virtual Guid ObjectId { get; set; }
    public virtual long Version { get; set; }
    public virtual Guid ChangeId { get; set; }
    public virtual Guid AuthorId { get; set; }
    public virtual DateTime AppliedAt { get; set; }
    public virtual string PayloadJson { get; set; } = "{}";

    public override bool Equals(object? obj)
    {
        return obj is ObjectChange other && other.ObjectId == ObjectId && other.Version == Version;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ObjectId, Version);
    }
}
