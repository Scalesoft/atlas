namespace Atlas.Server.Features.Projector.Domain;

/// <summary>A page whose published requirement blocks are predefined requirements of its space (spec §1.4).</summary>
public class ProjectorCatalogPage
{
    public virtual Guid PageId { get; set; }
    public virtual Guid MarkedById { get; set; }
    public virtual DateTime MarkedAt { get; set; }
}
