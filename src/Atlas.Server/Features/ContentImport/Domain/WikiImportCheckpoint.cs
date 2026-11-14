namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>The last revision produced by an import for one mapped page. Temporal: exactly one current checkpoint per import page; a matching source hash means the next import can skip.</summary>
public class WikiImportCheckpoint
{
    public virtual Guid Id { get; set; }

    public virtual Guid ImportPageId { get; set; }

    public virtual Guid RevisionId { get; set; }

    public virtual string SourceSha256 { get; set; } = "";

    public virtual DateTime ActiveFrom { get; set; }

    public virtual DateTime? ActiveTo { get; set; }
}
