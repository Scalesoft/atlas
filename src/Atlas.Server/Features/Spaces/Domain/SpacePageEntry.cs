namespace Atlas.Server.Features.Spaces.Domain;

/// <summary>Read-only page tree row: the page plus the titles and times of its current and published revisions.</summary>
public class SpacePageEntry
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual Guid? ParentId { get; set; }
    public virtual Guid OwnerId { get; set; }
    public virtual int Version { get; set; }
    public virtual int SortOrder { get; set; }
    public virtual bool IsTrashed { get; set; }
    public virtual bool IsTemplate { get; set; }
    public virtual bool IsPublished { get; set; }
    public virtual string? DraftTitle { get; set; }
    public virtual string? PublishedTitle { get; set; }
    public virtual DateTime? DraftActiveFrom { get; set; }
    public virtual DateTime? PublishedActiveFrom { get; set; }
}
