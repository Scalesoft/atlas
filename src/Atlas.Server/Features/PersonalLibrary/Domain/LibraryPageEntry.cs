namespace Atlas.Server.Features.PersonalLibrary.Domain;

/// <summary>Page-side projection for personal lists. No user dimension: favorites, visits, contributions and roles are joined per actor.</summary>
public class LibraryPageEntry
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual string SpaceName { get; set; } = "";
    public virtual Guid? ParentId { get; set; }
    public virtual Guid OwnerId { get; set; }
    public virtual int Version { get; set; }
    public virtual bool IsTrashed { get; set; }
    public virtual bool IsTemplate { get; set; }
    public virtual bool IsPublished { get; set; }
    public virtual Guid WorkingRevisionId { get; set; }
    public virtual Guid? PublishedRevisionId { get; set; }
    public virtual string WorkingTitle { get; set; } = "";
    public virtual DateTime WorkingActiveFrom { get; set; }
    public virtual string? PublishedTitle { get; set; }
    public virtual int? PublishedVersion { get; set; }
    public virtual DateTime? PublishedAt { get; set; }
    public virtual string? PublishedChangeSummary { get; set; }
    public virtual string? PublicationChannel { get; set; }
    public virtual Guid? PublicationApiTokenId { get; set; }
    public virtual bool? ParentIsTrashed { get; set; }
    public virtual bool? ParentIsTemplate { get; set; }
    public virtual bool? ParentIsPublished { get; set; }
    public virtual Guid? ParentOwnerId { get; set; }
}
