namespace Atlas.Server.Features.Administration.Domain;

/// <summary>Group identity. Its name history lives in UserGroupRevision, written by TR_UserGroup_History.</summary>
public class UserGroup
{
    public virtual Guid Id { get; set; }
    public virtual string Name { get; set; } = "";
}
