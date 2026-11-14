namespace Atlas.Server.Features.PersonalApi.Domain;

/// <summary>One interval of an account's API ceiling (ApiLevels). Immutable except ActiveTo, which closes it.</summary>
public class UserApiPermission
{
    public virtual Guid Id { get; set; }
    public virtual Guid UserId { get; set; }
    /// <summary>none, read or write (ApiLevels).</summary>
    public virtual string Level { get; set; } = ApiLevels.None;
    /// <summary>Derived and still stored (CK_UserApiPermission_LevelIssue) for the audit view and older readers.</summary>
    public virtual bool CanIssueTokens => Level != ApiLevels.None;
    public virtual Guid ChangedById { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
