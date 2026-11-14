namespace Atlas.Server.Features.PersonalLibrary.Domain;

public class PageVisit
{
    public virtual Guid Id { get; set; }
    public virtual Guid UserId { get; set; }
    public virtual Guid PageId { get; set; }
    public virtual DateTime LastVisitedAt { get; set; }
}
