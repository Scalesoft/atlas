namespace Atlas.Server.Features.PageTags.Domain;

public class PageTag
{
    public virtual Guid Id { get; set; }
    public virtual Guid PageId { get; set; }
    public virtual Guid TagId { get; set; }
    public virtual Guid CreatedById { get; set; }
    public virtual DateTime CreatedAt { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
