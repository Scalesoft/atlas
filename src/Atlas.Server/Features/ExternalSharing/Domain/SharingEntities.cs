namespace Atlas.Server.Features.ExternalSharing.Domain;

public class SpaceExternalSharingPolicy
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual bool Enabled { get; set; }
    public virtual Guid ChangedById { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}

public class PageShareLink
{
    /// <summary>The longest validity of a new link. src/Atlas.Web/src/sharing.ts (shareLinkMaxDays) mirrors it for the link flow.</summary>
    public const int MaxValidityDays = 30;

    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual Guid PageId { get; set; }
    public virtual Guid RevisionId { get; set; }
    public virtual Guid PolicyId { get; set; }
    public virtual Guid CreatedById { get; set; }
    public virtual string CreatorSecurityStamp { get; set; } = "";
    public virtual string Name { get; set; } = "";
    public virtual string TokenHash { get; set; } = "";
    /// <summary>The page was Živě as the link was created: the link shows its latest save while it stays live. Otherwise it shows <see cref="RevisionId"/>.</summary>
    public virtual bool FollowsLive { get; set; }
    public virtual DateTime ExpiresAt { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
