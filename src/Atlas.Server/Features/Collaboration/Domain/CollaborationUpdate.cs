namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>One durable relay update. The composite key is the identifier, so NHibernate needs value equality.</summary>
/// <remarks>
/// Generation is part of that identifier and migration 017 rewrites it via ON UPDATE CASCADE when
/// PageCollaboration.Generation changes, so a loaded log entity keeps a stale identity-map key. Never hold log
/// entities across a generation change: evict them or clear the session and re-read under the new generation.
/// </remarks>
public class CollaborationUpdate
{
    public virtual Guid PageId { get; set; }
    public virtual Guid Generation { get; set; }
    public virtual long Sequence { get; set; }
    public virtual Guid ClientUpdateId { get; set; }
    public virtual Guid UserId { get; set; }
    public virtual DateTime ReceivedAt { get; set; }
    public virtual byte[] Payload { get; set; } = [];
    /// <summary>3.6-H: the channel of a host-written update (api, mcp, assistant); null for a browser's (web).</summary>
    public virtual string? Channel { get; set; }
    public virtual Guid? ApiTokenId { get; set; }
    public virtual Guid? AiConversationId { get; set; }

    public override bool Equals(object? obj)
    {
        return obj is CollaborationUpdate other && other.PageId == PageId && other.Generation == Generation && other.Sequence == Sequence;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(PageId, Generation, Sequence);
    }
}
