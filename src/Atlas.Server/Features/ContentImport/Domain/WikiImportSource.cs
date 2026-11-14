namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>Stable identity of one Wiki.js export source. Everything else hangs off it.</summary>
public class WikiImportSource
{
    public virtual Guid Id { get; set; }

    public virtual string SourceKey { get; set; } = "";
}
