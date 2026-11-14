namespace Atlas.Server.Platform.Identity;

/// <summary>One row of the EffectivePageAccess view with the page state lists need: the highest current document role
/// (1 reader, 2 writer, 3 manager) of an active account on one page. Lists and search decide visibility through it.</summary>
public class PageAccessEntry
{
    public virtual Guid UserId { get; set; }
    public virtual Guid PageId { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual int Role { get; set; }
    public virtual bool IsTrashed { get; set; }
    public virtual bool IsPublished { get; set; }
    public virtual Guid OwnerId { get; set; }

    public override bool Equals(object? obj)
    {
        return obj is PageAccessEntry other && other.UserId == UserId && other.PageId == PageId;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(UserId, PageId);
    }
}
