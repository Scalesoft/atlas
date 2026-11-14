namespace Atlas.Markdown.Sync;

/// <summary>
/// Optional host-store capability for capturing a durable document lineage and its matching
/// stored state. It does not authorize access, enable replay, or fence legacy hub writes.
/// </summary>
public interface IDocumentRecoveryStore
{
    /// <summary>
    /// Capture the epoch, snapshot baseline and durable tail under one store read boundary.
    /// The caller owns all returned mutable buffers. A store may durably establish an epoch
    /// when this capability is first used; this must not implicitly bind a document schema.
    /// </summary>
    Task<DocumentRecoveryCheckpoint> LoadRecoveryCheckpointAsync(string documentId, CancellationToken ct);
}

/// <summary>
/// A stored snapshot plus its complete durable tail in one lineage. Epoch remains stable across
/// appends, compaction and restart; replacing a lineage requires a separate atomic store operation.
/// A matching epoch alone is not permission or proof that local edits are safe to replay.
/// </summary>
/// <param name="Epoch">Opaque durable lineage identity.</param>
/// <param name="SnapshotSequence">Sequence covered by Document.Snapshot; zero before the first snapshot.</param>
/// <param name="Document">Independently owned stored state and retry information.</param>
/// <param name="SchemaBound">Distinguishes an unbound empty document from explicit or implicit legacy mode.</param>
public sealed record DocumentRecoveryCheckpoint(
    string Epoch,
    long SnapshotSequence,
    StoredDocument Document,
    bool SchemaBound);
