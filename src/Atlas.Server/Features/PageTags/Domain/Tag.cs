namespace Atlas.Server.Features.PageTags.Domain;

public class Tag
{
    public virtual Guid Id { get; set; }
    public virtual string Name { get; set; } = "";
    public virtual string NormalizedName { get; set; } = "";
    public virtual DateTime CreatedAt { get; set; }
    public virtual Guid CreatedById { get; set; }
}
