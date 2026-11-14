namespace Atlas.Server.Features.Spaces.Domain;

/// <summary>An account's own pin choice for a space, versioned like PageFavorite. Pinned=false is an explicit unpin; no current row
/// is no choice of its own. Losing access or a policy change keeps the rows, so the earlier choice applies again.</summary>
public class SpacePin
{
    public virtual Guid Id { get; set; }
    public virtual Guid UserId { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual bool Pinned { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }

    /// <summary>The account's choice overrides None and Default, never Required.</summary>
    public static bool Effective(SpacePinPolicy policy, bool? choice)
    {
        return policy == SpacePinPolicy.Required || (choice ?? policy == SpacePinPolicy.Default);
    }
}
