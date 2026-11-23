using System.Collections.Concurrent;

namespace Atlas.Markdown.Sync;

/// <summary>A connected peer on a document.</summary>
internal sealed class Peer
{
    public required string ConnectionId { get; init; }
    public required CollabUser User { get; init; }
    public required bool CanWrite { get; set; }
    /// <summary>The newest revalidation (<see cref="DocumentAccessCoordinator.Revalidations"/>) applied to this peer; 0 after its join,
    /// whose lookups all precede a refresh that finds the peer.</summary>
    public long AccessRevalidation { get; set; }
    public int ProtocolVersion { get; init; } = 1;
    public long? AwarenessClientId { get; set; }
    public long AwarenessClock { get; set; }
    /// <summary>The same person's new connection took its awareness client id over (a reconnect): this one shows nothing any more.</summary>
    public bool AwarenessReplaced { get; set; }
    public long AwarenessWindow { get; set; }
    public int AwarenessCount { get; set; }
    public long UpdateWindow { get; set; }
    public int UpdateCount { get; set; }
    /// <summary>Sent an update on this connection: the snapshotter role goes to someone who writes, not to a tab left open.</summary>
    public bool HasWritten { get; set; }

    /// <summary>Monotonic join order — the snapshotter is the earliest-joined writer, one who has written first.</summary>
    public required long JoinOrder { get; init; }
}

/// <summary>
/// In-memory live state for one collaborative document. ALL mutation happens
/// under <see cref="Gate"/> (the relay is single-instance, ADR-002, so an
/// in-memory registry with per-doc serialization is sufficient — no backplane).
/// The relay never decodes CRDT content: <see cref="Snapshot"/> and each
/// <see cref="LogUpdate.Update"/> are opaque Yjs blobs.
/// </summary>
internal sealed class LiveDocument(string documentId)
{
    private readonly Queue<Guid> _recentUpdateOrder = new();
    private long _recentUpdatePayloadBytes;

    public string DocumentId { get; } = documentId;

    /// <summary>Serializes every mutation to this document's state (SemaphoreSlim(1,1)).</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Serializes initial durable seed writes without blocking lifecycle operations.</summary>
    public SemaphoreSlim SeedPersistenceGate { get; } = new(1, 1);

    // ---- CRDT state (opaque) ----
    public byte[]? Snapshot { get; set; }
    public ProjectionSetDto? LastProjection { get; set; }
    public MarkdownDocumentSchema? Schema { get; set; }
    public string? AuthorityEpoch { get; set; }

    /// <summary>Log records with seq in (<see cref="SnapshotSeq"/>, <see cref="Seq"/>].</summary>
    public List<LogUpdate> Log { get; } = [];

    /// <summary>Last assigned sequence number (monotonic; 0 = none yet).</summary>
    public long Seq { get; set; }

    /// <summary>Causality baseline of the current <see cref="Snapshot"/>.</summary>
    public long SnapshotSeq { get; set; }

    /// <summary>True once a snapshot has been pushed (or loaded from the store).</summary>
    public bool Seeded { get; set; }

    /// <summary>Store has been consulted for this doc (one-time cold load happened under the gate).</summary>
    public bool Loaded { get; set; }

    /// <summary>Current readable file, handed to the elected snapshotter when seeding an unseeded doc.</summary>
    public string? SeedMarkdown { get; set; }
    public System.Text.Json.JsonElement? SeedEditorJson { get; set; }

    /// <summary>Bounded durable idempotency window, keyed by client-generated update id.</summary>
    public Dictionary<Guid, long> RecentUpdateIds { get; } = [];

    /// <summary>Original bytes for safely validating and replaying retained idempotency entries.</summary>
    public Dictionary<Guid, StoredRecentUpdate> RecentUpdates { get; } = [];

    // ---- persistence bookkeeping (used by the flush service) ----
    /// <summary>A new snapshot was pushed and still needs an <c>IDocumentStore.FlushAsync</c>.</summary>
    public bool SnapshotDirty { get; set; }

    /// <summary>Incremented whenever a client snapshot is accepted; used by graceful shutdown acknowledgements.</summary>
    public long SnapshotRevision { get; private set; }

    /// <summary>True while the store reported a flush failure (drives the CZ "not saving" banner).</summary>
    public bool FlushDegraded { get; set; }

    /// <summary>
    /// True after an external-edit reseed (the readable file drifted while no session was live and
    /// was ADOPTED as the seed). The first snapshot flush that follows will legitimately see the
    /// stale on-disk ydoc header vs the reseeded file and report an "archived" drift — but that is
    /// the adoption, not a live-session conflict, so the external-edit toast is suppressed once and
    /// this flag cleared (ADR-018 §5). Distinguishes reseed-adoption from a real mid-session edit.
    /// </summary>
    public bool SuppressNextExternalEditReport { get; set; }

    /// <summary>
    /// Identity of the current CRDT baseline for <see cref="IDocumentContentGuard"/> caches. Replaced whenever the
    /// baseline is swapped (hydration, reseed) or an admitted payload failed to become durable. Call under the gate.
    /// </summary>
    public object ContentSession { get; private set; } = new();

    public void RenewContentSession() => ContentSession = new object();

    /// <summary>A server compaction (<see cref="DocumentCompactor"/>) of this document is in flight.</summary>
    public bool CompactionRunning { get; set; }

    /// <summary>After a failed server compaction, the head sequence before which it is not tried again.</summary>
    public long CompactionRetryAfter { get; set; }

    /// <summary>Per-document ordered broadcast tail. Assigned under <see cref="Gate"/>.</summary>
    public Task BroadcastTail { get; set; } = Task.CompletedTask;

    // ---- peers / election ----
    // ConcurrentDictionary so off-gate readers (SendAwareness membership check, the flush thread's
    // PeerCount/enumeration) are safe against under-gate structural mutations on other connections.
    // Logical decisions (election, seeding) still run under the gate for consistency.
    public ConcurrentDictionary<string, Peer> Peers { get; } = new(StringComparer.Ordinal);
    /// <summary>The client ids of collaborators the server shows (an agent writing, <see cref="LiveDocumentWriter.ShowAsync"/>), for the
    /// document's lifetime: no peer takes one for its awareness or writes structs as it (its text would be typed in with the AI caret).</summary>
    public ConcurrentDictionary<long, byte> ServerClients { get; } = new();
    public string? SnapshotterConnId { get; set; }
    private long _joinCounter;
    public long NextJoinOrder() => ++_joinCounter;

    // ---- single-writer seeding coordination ----
    /// <summary>An unseeded doc whose first joiner is parsing the seed markdown; concurrent joiners wait.</summary>
    public bool SeedInProgress { get; set; }

    /// <summary>
    /// The elected seeder has crossed into store I/O. Its ownership is retained across disconnect
    /// until that operation is reconciled, so a durable result cannot be overtaken by a new seed.
    /// </summary>
    public bool SeedPersistenceInFlight { get; set; }

    /// <summary>A failed/cancelled store call may have crossed a partial durability boundary.</summary>
    public bool SeedPersistenceRequiresReload { get; set; }

    public List<TaskCompletionSource<bool>> SeedWaiters { get; } = [];

    // ---- warm-keep eviction ----
    /// <summary>When the last peer left (null while peers are present). Evicted after WarmKeep.</summary>
    public DateTimeOffset? WarmSince { get; set; }

    public int PeerCount => Peers.Count;

    public bool TryGetSequence(Guid clientUpdateId, out long sequence)
        => RecentUpdateIds.TryGetValue(clientUpdateId, out sequence);

    public bool TryGetRecentUpdate(Guid clientUpdateId, out StoredRecentUpdate? update)
        => RecentUpdates.TryGetValue(clientUpdateId, out update);

    /// <summary>
    /// Reflect a store-confirmed append in live state. Returns true when peers need the update;
    /// ordinary retries already known in memory return false.
    /// </summary>
    public bool CommitDurableUpdate(
        AppendUpdateResult result,
        Guid clientUpdateId,
        string userId,
        DateTimeOffset receivedAtUtc,
        ReadOnlyMemory<byte> update)
    {
        if (clientUpdateId == Guid.Empty || result.Sequence <= 0)
            throw new InvalidDataException("Durable update identity or sequence is invalid.");
        if (TryGetSequence(clientUpdateId, out _)) return false;

        if (result.Duplicate && result.Sequence <= Seq)
        {
            RememberUpdate(clientUpdateId, result.Sequence, update.ToArray());
            return false;
        }

        if (result.Sequence != Seq + 1)
            throw new InvalidOperationException($"Expected update sequence {Seq + 1}, received {result.Sequence}.");

        var record = new LogUpdate(result.Sequence, clientUpdateId, userId, receivedAtUtc, update.ToArray());
        Seq = result.Sequence;
        Log.Add(record);
        RememberUpdate(record.ClientUpdateId, record.Seq, record.Update);
        return true;
    }

    /// <summary>
    /// Adopt a pushed snapshot: swap state, store markdown, drop log records the snapshot
    /// supersedes (seq &lt;= basedOnSeq) — records above it stay (they may not be in the
    /// snapshot yet; re-applying ones that are is a CRDT no-op). Marks dirty for the flush tick.
    /// </summary>
    public void AdoptSnapshot(byte[] state, ProjectionSetDto projections, long basedOnSeq)
    {
        if (basedOnSeq < SnapshotSeq)
            throw new InvalidOperationException(
                $"Snapshot baseline {basedOnSeq} is older than the current baseline {SnapshotSeq}.");
        if (basedOnSeq > Seq)
            throw new InvalidOperationException("Snapshot cannot cover updates beyond the current head.");
        var prepared = SnapshotValidation.Prepare(new DocSnapshot(state, basedOnSeq, projections));
        // No live fields change until cloning and validation have succeeded together.
        Snapshot = prepared.YjsState;
        LastProjection = prepared.Projections;
        SnapshotSeq = basedOnSeq;
        Seeded = true;
        Log.RemoveAll(r => r.Seq <= basedOnSeq);
        SnapshotDirty = true;
        SnapshotRevision++;
    }

    /// <summary>Load persisted state from the store (cold open).</summary>
    public void HydrateFrom(StoredDocument stored)
    {
        if (stored is null || stored.Seq < 0 || stored.Updates is null ||
            stored.RecentUpdateIds is null || stored.RecentUpdates is null)
            throw new InvalidDataException("Stored document envelope is invalid.");
        // Assemble the entire replacement off to the side: a bad retry index must not
        // partially replace a previously readable live document.
        var prepared = new LiveDocument(DocumentId);
        prepared.HydrateOwned(stored);
        RenewContentSession();
        Snapshot = prepared.Snapshot;
        LastProjection = null;
        SnapshotDirty = false;
        Seeded = prepared.Seeded;
        SnapshotSeq = prepared.SnapshotSeq;
        Seq = prepared.Seq;
        Log.Clear();
        Log.AddRange(prepared.Log);
        RecentUpdateIds.Clear();
        RecentUpdates.Clear();
        _recentUpdateOrder.Clear();
        foreach (var pair in prepared.RecentUpdateIds) RecentUpdateIds.Add(pair.Key, pair.Value);
        foreach (var pair in prepared.RecentUpdates) RecentUpdates.Add(pair.Key, pair.Value);
        foreach (var id in prepared._recentUpdateOrder) _recentUpdateOrder.Enqueue(id);
        _recentUpdatePayloadBytes = prepared._recentUpdatePayloadBytes;
    }

    private void HydrateOwned(StoredDocument stored)
    {
        Snapshot = stored.Snapshot?.ToArray();
        Seeded = stored.Snapshot is not null;
        Seq = stored.Seq;
        Log.Clear();
        long? previous = null;
        foreach (var record in stored.Updates)
        {
            if (record is null || record.Update is null || record.Seq <= 0 || record.Seq > stored.Seq ||
                (previous is { } sequence && record.Seq - 1 != sequence))
                throw new InvalidDataException("Stored update log is not a contiguous sequence.");
            Log.Add(record with { Update = record.Update.ToArray() });
            previous = record.Seq;
        }
        if (previous is { } last && last != stored.Seq)
            throw new InvalidDataException("Stored update log does not reach the declared head.");
        SnapshotSeq = Log.Count > 0 ? Log[0].Seq - 1 : stored.Seq;
        RecentUpdateIds.Clear();
        RecentUpdates.Clear();
        _recentUpdateOrder.Clear();
        _recentUpdatePayloadBytes = 0;
        foreach (var pair in stored.RecentUpdateIds.OrderBy(pair => pair.Value))
        {
            stored.RecentUpdates.TryGetValue(pair.Key, out var retained);
            if (retained is not null && retained.Sequence != pair.Value)
                throw new InvalidDataException("Stored recent-update sequence does not match its idempotency index.");
            RememberUpdate(pair.Key, pair.Value, retained?.Update?.ToArray());
        }
        foreach (var pair in stored.RecentUpdates.OrderBy(pair => pair.Value?.Sequence))
        {
            if (pair.Value?.Update is null) throw new InvalidDataException("Stored recent update payload is missing.");
            RememberUpdate(pair.Key, pair.Value.Sequence, pair.Value.Update.ToArray());
        }
        foreach (var record in Log)
            RememberUpdate(record.ClientUpdateId, record.Seq, record.Update);
    }

    /// <summary>The current writer peers ordered by join time (earliest first) — snapshotter candidates.</summary>
    public IEnumerable<Peer> WriterPeersByAge()
        => Peers.Values.Where(p => p.CanWrite).OrderByDescending(p => p.HasWritten).ThenBy(p => p.JoinOrder);

    /// <summary>
    /// Clear all CRDT state so the next load reseeds from the readable file (used when an external
    /// edit landed while the doc was warm with zero peers). Preserves object identity in the
    /// registry (peers are already empty). Call under the gate.
    /// </summary>
    public void ResetForReseed()
    {
        RenewContentSession();
        Snapshot = null;
        LastProjection = null;
        Log.Clear();
        Seq = 0;
        SnapshotSeq = 0;
        Seeded = false;
        Loaded = false;
        RecentUpdateIds.Clear();
        RecentUpdates.Clear();
        _recentUpdateOrder.Clear();
        _recentUpdatePayloadBytes = 0;
        SnapshotDirty = false;
        SeedMarkdown = null;
        SeedEditorJson = null;
        ClearSeedingState();
    }

    /// <summary>
    /// Abandon any in-flight single-writer seed: clear the flag, forget the elected seeder, and
    /// release every waiter with "retry" (false) so their JoinDocument fails fast and they re-join
    /// (whereupon the next writer becomes the seeder). Prevents the doc wedging when the elected
    /// seeder disconnects before pushing the initial snapshot. Call under the gate.
    /// </summary>
    public void ClearSeedingState()
    {
        SeedInProgress = false;
        SeedPersistenceInFlight = false;
        SeedPersistenceRequiresReload = false;
        SnapshotterConnId = null;
        foreach (var w in SeedWaiters) w.TrySetResult(false);
        SeedWaiters.Clear();
    }

    /// <summary>Approximate encoded state size for the 4 MiB cap (snapshot + pending log bytes; conservative).</summary>
    public long ApproxStateBytes()
    {
        long n = Snapshot?.Length ?? 0;
        foreach (var r in Log) n += r.Update.Length;
        return n;
    }

    private void RememberUpdate(Guid clientUpdateId, long sequence, byte[]? update)
    {
        if (clientUpdateId == Guid.Empty || sequence <= 0 || sequence > Seq)
            throw new InvalidDataException("Stored update identity or sequence is invalid.");
        if (RecentUpdateIds.TryGetValue(clientUpdateId, out var existingSequence))
        {
            if (existingSequence != sequence)
                throw new InvalidDataException("An update id is associated with multiple sequences.");
        }
        else
        {
            RecentUpdateIds.Add(clientUpdateId, sequence);
            _recentUpdateOrder.Enqueue(clientUpdateId);
        }

        if (update is not null && RecentUpdates.TryGetValue(clientUpdateId, out var retainedUpdate) &&
            !retainedUpdate.Update.AsSpan().SequenceEqual(update))
            throw new InvalidDataException("An update id is associated with different payloads.");
        if (update is not null && !RecentUpdates.ContainsKey(clientUpdateId))
        {
            RecentUpdates.Add(clientUpdateId, new StoredRecentUpdate(sequence, update));
            _recentUpdatePayloadBytes += update.Length;
        }

        while (_recentUpdateOrder.Count > RecentUpdateWindow.MaxEntries ||
               _recentUpdatePayloadBytes > RecentUpdateWindow.MaxPayloadBytes)
        {
            var expired = _recentUpdateOrder.Dequeue();
            RecentUpdateIds.Remove(expired);
            if (RecentUpdates.Remove(expired, out var retained))
                _recentUpdatePayloadBytes -= retained.Update.Length;
        }
    }
}
