namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>Stable mapping from a source-relative path to the page it created. A case change in the path is a conflict, not a rename.</summary>
public class WikiImportPage
{
    public virtual Guid Id { get; set; }

    public virtual Guid SourceId { get; set; }

    public virtual string SourcePath { get; set; } = "";

    public virtual Guid PageId { get; set; }
}
