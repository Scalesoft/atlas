using Atlas.Server.Features.Administration.Contracts;

namespace Atlas.Server.Features.Administration.Domain;

/// <summary>Current grants of a space with the display name of their subject. Read-only CQRS projection, not a stored copy.</summary>
public class SpaceGrantView
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual Guid? UserId { get; set; }
    public virtual Guid? GroupId { get; set; }
    public virtual string Name { get; set; } = "";
    public virtual int Role { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? StartAt { get; set; }
    public virtual DateTime? ExpiresAt { get; set; }

    public virtual GrantSummary ToSummary()
    {
        return new GrantSummary(Id, UserId, GroupId, Name, ((SpaceRole)Role).ToString(), AdministrationVersion.Format(ActiveFrom), StartAt, ExpiresAt);
    }
}
