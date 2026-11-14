namespace Atlas.Server.Features.PageOrganization.Domain;

/// <summary>Temporal history of a page's place in the tree, written with the page row (ContentRowEffects). Never saved as an entity.</summary>
public class PageOrganizationRevision
{
    public virtual Guid Id { get; set; }
    public virtual Guid PageId { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual Guid? ParentId { get; set; }
    public virtual int SortOrder { get; set; }
    public virtual bool IsTrashed { get; set; }
    public virtual DateTime? TrashedAt { get; set; }
    public virtual Guid? TrashedById { get; set; }
    public virtual Guid? TrashBatchId { get; set; }
    public virtual Guid? ActorId { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
