namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>One preview-through-commit run. Commit is idempotent on CompletedAt; the archive is re-hashed against ArchiveSha256 before it commits.</summary>
public class WikiImportBatch
{
    public virtual Guid Id { get; set; }

    public virtual Guid SourceId { get; set; }

    public virtual Guid CreatedBy { get; set; }

    public virtual DateTime CreatedAt { get; set; }

    public virtual DateTime ExpiresAt { get; set; }

    public virtual DateTime? CompletedAt { get; set; }

    public virtual string ArchiveSha256 { get; set; } = "";

    public virtual string OptionsJson { get; set; } = "";

    public virtual string ReportJson { get; set; } = "";
}
