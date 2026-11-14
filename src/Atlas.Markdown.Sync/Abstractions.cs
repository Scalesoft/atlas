using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace Atlas.Markdown.Sync;

/// <summary>
/// The authenticated collaborator, resolved by the host from the SignalR
/// handshake. Identity only — the relay never inspects CRDT content.
/// </summary>
/// <param name="Id">Stable user id (drives awareness/caret identity and snapshotter election).</param>
/// <param name="Name">Display name shown in presence.</param>
/// <param name="Color">Optional caret colour; the client falls back to a per-id palette.</param>
public sealed record CollabUser(string Id, string Name, string? Color = null);

/// <summary>Access a user has to a specific document.</summary>
public enum DocAccessLevel
{
    /// <summary>Not allowed to open the document at all.</summary>
    Denied,

    /// <summary>May open and receive updates, but not send updates or push snapshots (never elected snapshotter).</summary>
    Read,

    /// <summary>Full read/write; eligible to be the elected snapshotter.</summary>
    Write,
}

/// <summary>Per-document authorization result.</summary>
public sealed record DocAccess(DocAccessLevel Level)
{
    public bool CanRead => Level != DocAccessLevel.Denied;
    public bool CanWrite => Level == DocAccessLevel.Write;

    public static readonly DocAccess Denied = new(DocAccessLevel.Denied);
    public static readonly DocAccess ReadOnly = new(DocAccessLevel.Read);
    public static readonly DocAccess Write = new(DocAccessLevel.Write);
}

/// <summary>Human-readable projections attributed to one Yjs state by their producer.
/// Envelope validation does not prove their semantic agreement with opaque Yjs bytes.</summary>
public sealed record ProjectionSetDto(
    long SourceSequence,
    string StateSha256,
    string Markdown,
    JsonElement EditorJson,
    string PlainText);

/// <summary>A full opaque Yjs snapshot and its producer-supplied projections.</summary>
public sealed record DocSnapshot(
    byte[] YjsState,
    long CoveredSequence,
    ProjectionSetDto Projections);

/// <summary>A single durable update-log record.</summary>
/// <param name="Seq">Server-assigned monotonic document sequence.</param>
/// <param name="ClientUpdateId">Client-generated idempotency key.</param>
/// <param name="UserId">Authenticated author id captured by the server.</param>
/// <param name="ReceivedAtUtc">Server receipt time.</param>
/// <param name="Update">Opaque Yjs update bytes.</param>
public sealed record LogUpdate(
    long Seq,
    Guid ClientUpdateId,
    string UserId,
    DateTimeOffset ReceivedAtUtc,
    byte[] Update);

/// <summary>Result of an atomic durable update append.</summary>
/// <param name="Sequence">The original durable sequence assigned to the client update id.</param>
/// <param name="Duplicate">True when the same client update id was already durable.</param>
public sealed record AppendUpdateResult(long Sequence, bool Duplicate);

/// <summary>
/// Durable identity of a recently accepted update. The original opaque bytes are retained so an
/// idempotent retry can be validated and, when necessary, replayed after log compaction.
/// </summary>
public sealed record StoredRecentUpdate(long Sequence, byte[] Update);

/// <summary>
/// Bounds of the durable idempotency window shared by the relay and host stores. A store should
/// retain at least this window so a retry the relay still recognizes can be validated after restart.
/// </summary>
public static class RecentUpdateWindow
{
    /// <summary>Maximum number of retained client update ids.</summary>
    public const int MaxEntries = 4096;

    /// <summary>Maximum total bytes of retained original update payloads.</summary>
    public const int MaxPayloadBytes = 16 * 1024 * 1024;
}

/// <summary>
/// Outcome of an <see cref="IDocumentStore.FlushAsync"/>. The store reports back
/// whether the readable file had drifted underneath the live session (an external
/// edit) and was archived before being overwritten with the collab projection, so
/// the relay can notify participants (ADR-018 §5 — live session wins, external
/// version archived, never a silent loss of either side).
/// </summary>
public sealed record FlushResult(bool ExternalEditArchived)
{
    public static readonly FlushResult None = new(false);

    /// <summary>
    /// The store no longer holds this document: the host replaced it (a new generation after a save, restore or discard)
    /// or removed it. The snapshot can never be stored, so the relay drops it instead of retrying it every tick; what its
    /// edits become was decided by the host when it replaced the document.
    /// </summary>
    public static readonly FlushResult Superseded = new(false) { IsSuperseded = true };

    public bool IsSuperseded { get; init; }
}

/// <summary>What the host store returns when a document is first opened (cold or reseed).</summary>
/// <param name="Snapshot">Last persisted full Yjs state, or null if the document was never seeded.</param>
/// <param name="Updates">Persisted update-log records since the snapshot, in seq order.</param>
/// <param name="Seq">Seq of the last log record included (causality baseline).</param>
/// <param name="SeedMarkdown">
/// The current readable file, handed to the elected snapshotter to seed an unseeded document.
/// </param>
/// <param name="ExternalChange">
/// True when the readable file changed underneath the stored state (e.g. a VS Code edit while
/// no session was live) — the caller discards the stale state and takes the reseed path.
/// </param>
public sealed record StoredDocument(
    byte[]? Snapshot,
    IReadOnlyList<LogUpdate> Updates,
    long Seq,
    string? SeedMarkdown,
    bool ExternalChange,
    IReadOnlyDictionary<Guid, long> RecentUpdateIds)
{
    /// <summary>Optional lossless rich document seed, preferred to the Markdown projection.</summary>
    public JsonElement? SeedEditorJson { get; init; }

    /// <summary>Persisted versioned identity; null means the legacy schema mode.</summary>
    public MarkdownDocumentSchema? Schema { get; init; }

    /// <summary>
    /// Bounded durable retry window including the original update bytes. Stores that only provide
    /// <see cref="RecentUpdateIds"/> remain source-compatible, but the relay fails closed when an
    /// old entry cannot be validated or safely replayed.
    /// </summary>
    public IReadOnlyDictionary<Guid, StoredRecentUpdate> RecentUpdates { get; init; }
        = new Dictionary<Guid, StoredRecentUpdate>();
}

/// <summary>
/// Host-owned persistence for a collaborative document. The host owns paths,
/// file formats, atomicity, and archive policy; the relay owns framing, seq
/// assignment, and the in-memory live state.
/// </summary>
public interface IDocumentStore
{
    /// <summary>Load the persisted state for a document on first open (snapshot + log tail + seed).</summary>
    Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct);

    /// <summary>
    /// Atomically establish a new document's schema, or reject a different existing identity.
    /// Persist before completing, even before the initial snapshot exists. Null explicitly means
    /// legacy mode; existing opaque data must never acquire an inferred profile. The default keeps
    /// legacy stores source-compatible but rejects versioned sessions until durable binding is implemented.
    /// </summary>
    async Task<MarkdownDocumentSchema?> BindSchemaAsync(string documentId, MarkdownDocumentSchema? schema, CancellationToken ct)
    {
        if (schema is not null)
            throw new DocumentSchemaException("The document store does not support durable schema negotiation.");
        var stored = await LoadAsync(documentId, ct).ConfigureAwait(false);
        DocumentSchemaCompatibility.EnsureCompatible(stored?.Schema, null);
        return null;
    }

    /// <summary>
    /// Durably persist a new full snapshot (readable markdown + binary state); truncates the
    /// persisted log to records with seq &gt; <paramref name="snapshot"/>.CoveredSequence. Returns a
    /// <see cref="FlushResult"/> flagging whether the readable file had been edited externally
    /// since the last flush and was archived before being overwritten (ADR-018 §5).
    /// </summary>
    Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct);

    /// <summary>
    /// Atomically deduplicate, assign a sequence and durably append one update. Successful completion
    /// is the durability boundary used by the hub before it acknowledges or broadcasts. An
    /// implementation that recognizes <paramref name="clientUpdateId"/> as a duplicate must reject
    /// a different <paramref name="update"/> payload; an idempotency key never aliases two updates.
    /// </summary>
    Task<AppendUpdateResult> AppendUpdateAsync(
        string documentId,
        Guid clientUpdateId,
        string userId,
        DateTimeOffset receivedAtUtc,
        ReadOnlyMemory<byte> update,
        CancellationToken ct);

    /// <summary><see cref="AppendUpdateAsync(string, Guid, string, DateTimeOffset, ReadOnlyMemory{byte}, CancellationToken)"/> of an update a host
    /// wrote (<see cref="LiveDocumentWriter"/>), with where it came from; a store without attribution ignores the origin.</summary>
    Task<AppendUpdateResult> AppendUpdateAsync(string documentId, Guid clientUpdateId, string userId, DateTimeOffset receivedAtUtc,
        ReadOnlyMemory<byte> update, UpdateOrigin? origin, CancellationToken ct)
        => AppendUpdateAsync(documentId, clientUpdateId, userId, receivedAtUtc, update, ct);

    /// <summary>
    /// Cheap check (stat/hash) run on every join, warm-kept documents included: did the readable
    /// file change underneath the stored state since the last flush? Returns false when unsupported.
    /// </summary>
    Task<bool> HasExternalChangeAsync(string documentId, CancellationToken ct) => Task.FromResult(false);

    /// <summary>The last peer left the document (it stays warm in memory): a host that stores live documents itself stores what is unsaved now.</summary>
    Task LastPeerLeftAsync(string documentId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Thrown by a store when the host replaced the document (a new generation after a restore, a source replace or a trash): the writer's
/// update can never be stored, so the relay answers <c>[SUPERSEDED]</c>, which the client treats as final and rejoins the current document.
/// </summary>
public sealed class DocumentSupersededException(string message) : Exception(message);

/// <summary>
/// Host-owned identity + per-document authorization, resolved from the SignalR
/// handshake context (the ambient <c>IHttpContextAccessor.HttpContext</c> is null
/// inside hub invocations, so identity MUST come from the handshake).
/// </summary>
public interface ICollabAuthenticator
{
    /// <summary>Resolve the connected user from the handshake, or null to reject the connection.</summary>
    Task<CollabUser?> AuthenticateAsync(HttpContext handshakeContext, CancellationToken ct);

    /// <summary>Current access to a document. Called at join, write, publication and periodic live refresh;
    /// implementations must reflect committed ACL changes and support cancellation.</summary>
    Task<DocAccess> AuthorizeAsync(CollabUser user, string documentId, CancellationToken ct);
}

/// <summary>Options for the TipTap sync backend.</summary>
public sealed class MarkdownSyncOptions
{
    /// <summary>Idle peer ACL refresh cadence. Point-of-use authorization does not wait for this timer.</summary>
    public TimeSpan AccessRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum wait per ACL lookup; failure or timeout does not grant access.</summary>
    public TimeSpan AuthorizationTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Bounds active ACL calls, including implementations that ignore cancellation.</summary>
    public int MaxConcurrentAuthorizations { get; set; } = 32;

    /// <summary>How long a document with zero peers is kept warm in memory before eviction. Default 30 s.</summary>
    public TimeSpan WarmKeep { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Disk flush cadence. Default 1 s (bounds CRDT loss on crash to ~1 s).</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a concurrent joiner waits for the seeding snapshotter's first snapshot. Default 5 s.</summary>
    public TimeSpan SeedWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum graceful-shutdown wait for requested client snapshots. Default 2 s.</summary>
    public TimeSpan ShutdownSnapshotWait { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Number of update-log records above the snapshot at which the relay merges them into a snapshot itself
    /// (<see cref="DocumentCompactor"/>) because the elected client has not. Needs the managed content guard; 0 turns it off.
    /// Such a snapshot carries the editor JSON only: its Markdown and plain text projections are empty. Default 1024.
    /// </summary>
    public int ServerCompactionUpdates { get; set; } = 1024;

    /// <summary>A host that stores live documents itself (Atlas 3.6) stores one at the latest this long after its first unsaved update. Default 30 s.</summary>
    public TimeSpan ServerCheckpointInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Reject updates that would grow a document's encoded state past this. Default 4 MiB.</summary>
    public long MaxDocumentBytes { get; set; } = 4L * 1024 * 1024;
}
