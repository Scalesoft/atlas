using Atlas.Server.Features.Administration.Contracts;

namespace Atlas.Server.Features.Administration.Domain;

/// <summary>Current members of a group with their account facts. Read-only CQRS projection, not a stored copy.</summary>
public class GroupMemberView
{
    public virtual Guid Id { get; set; }
    public virtual Guid GroupId { get; set; }
    public virtual Guid UserId { get; set; }
    public virtual string Username { get; set; } = "";
    public virtual string DisplayName { get; set; } = "";
    public virtual bool IsActive { get; set; }
    public virtual DateTime ActiveFrom { get; set; }

    /// <summary>The wire contract identifies a member by the account id, not by the membership row.</summary>
    public virtual GroupMemberSummary ToSummary()
    {
        return new GroupMemberSummary(UserId, Username, DisplayName, IsActive, AdministrationVersion.Format(ActiveFrom));
    }
}
