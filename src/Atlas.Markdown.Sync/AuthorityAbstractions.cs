namespace Atlas.Markdown.Sync;

/// <summary>Optional V2 capability. V1 stores remain source compatible and cannot advertise authority.</summary>
public interface IDocumentAuthorityStore
{
    Task<DocumentAuthorityState?> LoadAuthorityAsync(string documentId, CancellationToken ct);
    Task<DocumentAuthorityState> ActivateAuthorityAsync(string documentId, MarkdownDocumentSchema schema,
        string fragment, long expectedLegacySequence, DocSnapshot baseline, CancellationToken ct);
    Task<DocumentUpdateReceipt> CommitAuthorityAsync(string documentId, string epoch, long expectedSequence,
        Guid updateId, string sha256, string userId, DateTimeOffset receivedAtUtc, ReadOnlyMemory<byte> update,
        DocSnapshot candidate, CancellationToken ct);
}

/// <summary>Optional maintenance capability: caller independently verifies replay before pruning payloads.</summary>
public interface IDocumentAuthorityCompactionStore
{
    Task<DocumentAuthorityState> CompactAuthorityAsync(string documentId, string epoch,
        DocSnapshot verifiedSnapshot, CancellationToken ct);
}
public sealed record DocumentUpdateReceipt(int ProtocolVersion, string Epoch, string UpdateId, string Sha256,
    long Sequence, long PersistedSequence);
public sealed record AuthorityUpdateRecord(DocumentUpdateReceipt Receipt, string UserId,
    DateTimeOffset ReceivedAtUtc, byte[] Update);
public sealed record DocumentAuthorityState(int ProtocolVersion, string DocumentId, string Epoch,
    MarkdownDocumentSchema Schema, string Fragment, DocSnapshot Baseline, DocSnapshot Snapshot,
    IReadOnlyList<AuthorityUpdateRecord> Records)
{
    public long Sequence => Snapshot.CoveredSequence;
    public long? OriginSequence { get; init; }
}
public sealed record AuthorityUpdateRequest(string Epoch, string UpdateId, string Sha256, byte[] Update);
public sealed record AuthorityUpdateDelivery(string Epoch, string UpdateId, string Sha256, byte[] Update,
    long Sequence, long PersistedSequence) { public int ProtocolVersion => 2; }
public sealed record AuthorityLimits(long MaxUpdateBytes, long MaxDocumentBytes);
public sealed record AuthorityCapabilities(int ProtocolVersion, bool Active, string Authority, string Recovery,
    long MaxUpdateBytes);
public sealed record AuthorityJoinResult(int ProtocolVersion, string Epoch, MarkdownDocumentSchema Schema,
    string Fragment, byte[] Snapshot, long SnapshotSequence, long Seq, long PersistedSequence, bool CanWrite,
    AuthorityLimits Limits, string Authority = "server", string Recovery = "durable-receipts");

/// <summary>Trusted external reducer. Candidates must be complete, schema-valid and server-projected.</summary>
public interface IDocumentAuthorityReducer
{
    Task<DocSnapshot> ReduceAsync(MarkdownDocumentSchema schema, string fragment, byte[]? baseline,
        IReadOnlyList<byte[]> updates, string? seedMarkdown, long sequence, CancellationToken ct);
}

public sealed class DocumentAuthorityOptions
{
    public string Fragment { get; set; } = "default";
    public long MaxUpdateBytes { get; set; } = 1024 * 1024;
    public int MaxAwarenessBytes { get; set; } = 16 * 1024;
    public int MaxAwarenessStates { get; set; } = 1;
    public int MaxAwarenessPerSecond { get; set; } = 30;
    /// <summary>Content updates one connection may send per second (protocol 1, SendUpdate); more are refused with [RATE_LIMITED], which the
    /// editor retries. Typing sends one at a time and merges what waits, so a person stays far below it.</summary>
    public int MaxUpdatesPerSecond { get; set; } = 20;
}



