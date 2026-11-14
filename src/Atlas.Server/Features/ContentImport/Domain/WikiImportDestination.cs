namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>The quarantine space a source currently lands in. Temporal: at most one current destination per source.</summary>
public class WikiImportDestination
{
    public virtual Guid Id { get; set; }

    public virtual Guid SourceId { get; set; }

    public virtual Guid SpaceId { get; set; }

    public virtual DateTime ActiveFrom { get; set; }

    public virtual DateTime? ActiveTo { get; set; }
}
