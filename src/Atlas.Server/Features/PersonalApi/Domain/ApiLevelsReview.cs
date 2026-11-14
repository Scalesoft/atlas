namespace Atlas.Server.Features.PersonalApi.Domain;

/// <summary>The upgrade to token rights (migration 2026100600) converted Accounts accounts to level publish and Tokens write tokens to
/// draft + publish (all of which 2026110684 turned into write). Until a WikiAdmin confirms it, the security overview shows the item api_levels_review.</summary>
public class ApiLevelsReview
{
    public virtual Guid Id { get; set; }
    public virtual DateTime CreatedAt { get; set; }
    public virtual int Accounts { get; set; }
    public virtual int Tokens { get; set; }
    public virtual Guid? ConfirmedById { get; set; }
    public virtual DateTime? ConfirmedAt { get; set; }
}
