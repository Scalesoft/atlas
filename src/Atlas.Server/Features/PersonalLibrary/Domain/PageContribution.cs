namespace Atlas.Server.Features.PersonalLibrary.Domain;

/// <summary>Rebuildable projection of revision authorship written with each revision (ContentRowEffects). The application only reads it.</summary>
public class PageContribution
{
    public virtual Guid UserId { get; set; }
    public virtual Guid PageId { get; set; }
    public virtual int LastRevisionVersion { get; set; }

    public override bool Equals(object? obj)
    {
        return obj is PageContribution other && other.UserId == UserId && other.PageId == PageId;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(UserId, PageId);
    }
}
