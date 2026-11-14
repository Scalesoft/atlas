namespace Atlas.Server.Features.PersonalLibrary.Domain;

public class PageFavorite
{
    public virtual Guid Id { get; set; }
    public virtual Guid UserId { get; set; }
    public virtual Guid PageId { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
