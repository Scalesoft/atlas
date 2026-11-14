namespace Atlas.Server.Platform.Identity;

/// <summary>Currently effective membership; externally managed sources carry their verification deadline.</summary>
public class GroupMembership
{
    public virtual Guid Id { get; set; }
    public virtual Guid GroupId { get; set; }
    public virtual Guid UserId { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
    public virtual DateTime? VerifiedUntil { get; set; }
}
