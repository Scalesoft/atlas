namespace Atlas.Server.Features.PageOrganization.Domain;

/// <summary>Read-only per-page projection for the tree, the trash list and sibling title collisions. Carries both titles so one view serves readers and writers.</summary>
public class PageTreeEntry
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual Guid? ParentId { get; set; }
    public virtual Guid OwnerId { get; set; }
    public virtual int Version { get; set; }
    public virtual int SortOrder { get; set; }
    public virtual bool IsTrashed { get; set; }
    public virtual bool IsTemplate { get; set; }
    public virtual DateTime? TrashedAt { get; set; }
    public virtual Guid? TrashedById { get; set; }
    public virtual Guid? TrashBatchId { get; set; }
    public virtual bool IsPurged { get; set; }
    public virtual bool IsPublished { get; set; }
    public virtual string WorkingTitle { get; set; } = "";
    public virtual string? PublishedTitle { get; set; }
    public virtual bool SortChildrenByTitle { get; set; }
}
