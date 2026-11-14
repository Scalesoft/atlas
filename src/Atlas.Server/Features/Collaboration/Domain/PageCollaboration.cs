namespace Atlas.Server.Features.Collaboration.Domain;

public class PageCollaboration
{
    public virtual Guid PageId { get; set; }
    public virtual Guid Generation { get; set; }
    public virtual Guid BaseRevisionId { get; set; }
    public virtual long Sequence { get; set; }
    public virtual long? LastSavedSequence { get; set; } = 0;
    public virtual long SnapshotSequence { get; set; }
    /// <summary>Updates above <see cref="SnapshotSequence"/> and their payload bytes: the pending log an append is bounded by.</summary>
    public virtual int PendingCount { get; set; }
    public virtual long PendingBytes { get; set; }
    public virtual bool SchemaBound { get; set; }
    public virtual string? SchemaJson { get; set; }
    public virtual byte[]? State { get; set; }
    public virtual string? StateSha256 { get; set; }
    public virtual string? ProjectionJson { get; set; }
    /// <summary>The sequence of the newest snapshot refused as page content, with <see cref="Refusal"/>; null when none since the last stored one.</summary>
    public virtual long? RefusedSequence { get; set; }
    /// <summary>The refusal as JSON (status, code, message, path), never the content.</summary>
    public virtual string? Refusal { get; set; }
    public virtual DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
