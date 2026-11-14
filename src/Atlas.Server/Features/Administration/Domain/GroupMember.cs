namespace Atlas.Server.Features.Administration.Domain;

/// <summary>Writable membership interval. Platform reads the same table through the immutable GroupMembership projection.</summary>
public class GroupMember
{
    public virtual Guid Id { get; set; }
    public virtual Guid GroupId { get; set; }
    public virtual Guid UserId { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
