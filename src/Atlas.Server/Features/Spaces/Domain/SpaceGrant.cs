namespace Atlas.Server.Features.Spaces.Domain;

/// <summary>One temporal grant of a space role to a user or a group. ActiveTo NULL is the current interval.</summary>
public class SpaceGrant
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual Guid? UserId { get; set; }
    public virtual Guid? GroupId { get; set; }
    public virtual int Role { get; set; }
    public virtual DateTime? StartAt { get; set; }
    public virtual DateTime? ExpiresAt { get; set; }
    public virtual DateTime ActiveFrom { get; set; } = DateTime.UtcNow;
    public virtual DateTime? ActiveTo { get; set; }
}
