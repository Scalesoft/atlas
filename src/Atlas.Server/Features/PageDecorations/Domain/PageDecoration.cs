namespace Atlas.Server.Features.PageDecorations.Domain;

public class PageDecoration
{
    public virtual Guid PageId { get; set; }
    public virtual Guid RevisionId { get; set; }
    public virtual string? Icon { get; set; }
    public virtual string? Color { get; set; }
}
