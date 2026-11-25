using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Result of a successful <see cref="MarkdownSyncHub.JoinDocument"/>. All byte
/// payloads are opaque Yjs blobs (base64 over the JSON hub protocol).
/// </summary>
/// <param name="Snapshot">Last pushed full state; null ONLY for the seeding snapshotter of an unseeded doc.</param>
/// <param name="Updates">Update log since the snapshot, in seq order.</param>
/// <param name="Seq">Seq of the last log record included — the causality baseline for PushSnapshot.</param>
/// <param name="SeedMarkdown">Only set for the elected snapshotter of an unseeded doc (first open / cold reseed).</param>
/// <param name="IsSnapshotter">This connection was elected the snapshot renderer.</param>
public sealed record JoinResult(
    byte[]? Snapshot,
    byte[][] Updates,
    long Seq,
    string? SeedMarkdown,
    bool IsSnapshotter)
{
    public MarkdownDocumentSchema? Schema { get; init; }
    /// <summary>Lossless rich seed supplied only to the initial seeding writer.</summary>
    public System.Text.Json.JsonElement? SeedEditorJson { get; init; }
    /// <summary>Effective per-document write permission for this connection.</summary>
    public bool CanWrite { get; init; }
    /// <summary>"gzip" when <see cref="Snapshot"/> is gzip-compressed (the connection asked with <c>?snapshot=gzip</c>); null when raw.</summary>
    public string? SnapshotEncoding { get; init; }
    /// <summary>
    /// The seed (<c>{"markdown":…,"editorJson":…}</c> as UTF-8 JSON) gzip-compressed for a connection that asked with
    /// <c>?seed=gzip</c>; <see cref="SeedMarkdown"/> and <see cref="SeedEditorJson"/> are then null.
    /// </summary>
    public byte[]? Seed { get; init; }
    /// <summary>
    /// The relay derives the snapshot's projections itself (it materializes the editor JSON, and its own snapshots carry empty
    /// Markdown and plain text, <see cref="DocumentCompactor"/>): the snapshotter may push the state alone (<c>PushSnapshotGzip</c>
    /// with empty projections) instead of rendering the whole page after every batch of changes.
    /// </summary>
    public bool ProjectionsDerived { get; init; }
}

/// <summary>
/// Relay-and-persist hub for Atlas Markdown collaborative editing. One SignalR
/// group per document. The relay itself understands framing only; content is decoded only by an
/// opted-in <see cref="IDocumentContentGuard"/> before it is stored or broadcast. Identity comes from the handshake (not <c>ICurrentActor</c>,
/// which is null inside hub invocations). See ADR-018 §2 for the wire protocol.
/// </summary>
public sealed partial class MarkdownSyncHub(
    DocumentRegistry registry,
    IDocumentStore store,
    ICollabAuthenticator authenticator,
    DocumentAccessCoordinator accessCoordinator,
    DocumentAuthorityCoordinator authorityCoordinator,
    DocumentAuthorityOptions authorityOptions,
    MarkdownSyncOptions options,
    IDocumentContentGuard contentGuard,
    DocumentCompactor compactor,
    TimeProvider clock,
    ILogger<MarkdownSyncHub> log) : Hub
{
    private const string UserKey = "tiptap.user";
    private const string JoinedKey = "tiptap.joined";

    // client-callable method names (server → client)
    internal const string ReceiveUpdate = "ReceiveUpdate";
    internal const string ReceiveAwareness = "ReceiveAwareness";
    internal const string SnapshotterChanged = "SnapshotterChanged";
    internal const string RequestSnapshot = "RequestSnapshot";
    internal const string FlushDegraded = "FlushDegraded";
    internal const string ExternalEditArchived = "ExternalEditArchived";
    internal const string SnapshotStored = "SnapshotStored";

    internal static string GroupName(string documentId) => "tiptap:" + documentId;

    /// <summary>
    /// Advisory group signal sent after a snapshot reached durable storage, so hosts can re-read
    /// projections without polling. A failed send is only logged: durability already happened and
    /// the next flush repeats the announcement for a newer sequence.
    /// </summary>
    internal static async Task AnnounceSnapshotStoredAsync(DocumentAccessCoordinator access, ILogger log, LiveDocument doc, long sequence)
    {
        try
        {
            await access.SendToReadersAsync(doc, SnapshotStored, [doc.DocumentId, sequence], ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            log.LogWarning(error, "SnapshotStored announcement failed for {DocumentId} seq {Sequence}", doc.DocumentId, sequence);
        }
    }

    // ---- connection lifecycle ----

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var user = http is null
            ? null
            : await authenticator.AuthenticateAsync(http, Context.ConnectionAborted).ConfigureAwait(false);
        if (user is null)
        {
            log.LogInformation("Collab connection {ConnectionId} rejected — no authenticated user",
                Context.ConnectionId);
            Context.Abort();
            return;
        }

        Context.Items[UserKey] = user;
        Context.Items[JoinedKey] = new HashSet<string>(StringComparer.Ordinal);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var documentId in JoinedDocs().ToArray())
        {
            await LeaveInternalAsync(documentId, connectionGone: true).ConfigureAwait(false);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    // ---- client → server ----

    public async Task<JoinResult> JoinDocument(string documentId)
        => CompressSnapshot(await JoinCoreAsync(documentId, null).ConfigureAwait(false));

    public async Task<JoinResult> JoinDocumentWithSchema(string documentId, MarkdownDocumentSchema schema)
    {
        if (schema is null) throw new HubException("[SCHEMA_MISMATCH] A versioned schema is required.");
        MarkdownDocumentSchema normalized;
        try { normalized = DocumentSchemaCompatibility.Normalize(schema); }
        catch (DocumentSchemaException error) { throw new HubException(error.Message); }
        return CompressSnapshot(await JoinCoreAsync(documentId, normalized).ConfigureAwait(false));
    }

    /// <summary>
    /// The full state is the one large message of a join (1.1 MB for an 830 KB page, a third more as base64) and compresses
    /// to about a quarter. Only a connection that declared it can inflate it (<c>?snapshot=gzip</c>) gets it compressed, so older
    /// clients keep the raw form. Done outside the document gate; the snapshot array is never mutated in place.
    /// </summary>
    private JoinResult CompressSnapshot(JoinResult result)
    {
        var query = Context.GetHttpContext()?.Request.Query;
        result = result with { ProjectionsDerived = compactor.Materializer is not null && options.ServerCompactionUpdates > 0 };
        if (query?["seed"].ToString() == "gzip" && (result.SeedMarkdown is not null || result.SeedEditorJson is not null))
        {
            // The seed is the whole page as JSON (140–210 KB for a deployment page) and compresses like it.
            var seed = JsonSerializer.SerializeToUtf8Bytes(new { markdown = result.SeedMarkdown, editorJson = result.SeedEditorJson });
            result = result with { Seed = Gzip(seed), SeedMarkdown = null, SeedEditorJson = null };
        }
        if (result.Snapshot is null || query?["snapshot"].ToString() != "gzip") return result;
        return result with { Snapshot = Gzip(result.Snapshot), SnapshotEncoding = "gzip" };
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Optimal))
            gzip.Write(bytes);
        return compressed.ToArray();
    }

    private async Task<JoinResult> JoinCoreAsync(string documentId, MarkdownDocumentSchema? schema)
    {
        if (!DocumentId.TryParse(documentId, out var parsedDocumentId))
            throw new HubException("Document id is invalid.");
        documentId = parsedDocumentId!.Value;

        var user = RequireUser();
        // One authorization serves this join, taken again under the gate only when a revalidation began since
        // (DocumentAccessCoordinator.CurrentAsync); it lives in this call and never serves another join or connection.
        var revalidation = accessCoordinator.Revalidations;
        var access = await accessCoordinator.AuthorizeAsync(user, documentId, Context.ConnectionAborted)
            .ConfigureAwait(false);
        if (!access.CanRead) throw new HubException("[FORBIDDEN] Document access denied.");

        // Retry loop guards the eviction TOCTOU: a warm doc can be evicted between GetOrCreate and
        // locking its gate. If the instance we locked is no longer the registered one, we start over
        // on the canonical instance rather than binding to an orphan (split-brain).
        while (true)
        {
            var doc = registry.GetOrCreate(documentId);
            TaskCompletionSource<bool>? seedWaiter = null;

            await doc.Gate.WaitAsync(Context.ConnectionAborted).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(registry.Current(documentId), doc))
                    continue; // evicted underneath us → retry on the canonical instance

                (access, revalidation) = await accessCoordinator.CurrentAsync(user, documentId, access, revalidation, Context.ConnectionAborted)
                    .ConfigureAwait(false);
                if (!access.CanRead) throw new HubException("[FORBIDDEN] Document access denied.");
                await authorityCoordinator.EnsureV1UnderGateAsync(doc, Context.ConnectionAborted).ConfigureAwait(false);
                await registry.EnsureLoadedAsync(doc, Context.ConnectionAborted).ConfigureAwait(false);
                if (!doc.Seeded && !doc.SeedInProgress && !access.CanWrite)
                    throw new HubException($"[READ_ONLY] A writer is required to initialize document '{documentId}'.");

                try
                {
                    // Binding is durable BEFORE group membership, hydration, seeding or writes.
                    // A rejected client must never receive content its schema could truncate.
                    // Only a writer binds; a reader is checked against what a writer bound (a reader can join only a doc a writer seeded).
                    var bound = access.CanWrite
                        ? await store.BindSchemaAsync(documentId, schema, Context.ConnectionAborted).ConfigureAwait(false)
                        : doc.Schema ?? (await store.LoadAsync(documentId, Context.ConnectionAborted).ConfigureAwait(false))?.Schema;
                    DocumentSchemaCompatibility.EnsureCompatible(bound, schema);
                    doc.Schema = bound;
                }
                catch (DocumentSchemaException error) { throw new HubException(error.Message); }
                await registry.ReseedIfExternallyChangedWarmAsync(doc, Context.ConnectionAborted).ConfigureAwait(false);

                // Loading/binding may await external storage. Recheck before publishing hydration.
                (access, revalidation) = await accessCoordinator.CurrentAsync(user, documentId, access, revalidation, Context.ConnectionAborted)
                    .ConfigureAwait(false);
                if (!access.CanRead) throw new HubException("[FORBIDDEN] Document access denied.");
                if (!doc.Seeded && !doc.SeedInProgress && !access.CanWrite)
                    throw new HubException($"[READ_ONLY] A writer is required to initialize document '{documentId}'.");

                // Group-add + log capture happen in the SAME critical section as SendUpdate's
                // append, so no update can slip between "log captured for JoinResult" and "this
                // connection is in the group" (ADR-018 §2 step 1).
                await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(documentId), Context.ConnectionAborted)
                    .ConfigureAwait(false);
                doc.Peers[Context.ConnectionId] = new Peer
                {
                    ConnectionId = Context.ConnectionId,
                    User = user,
                    Color = doc.ColorFor(user),
                    CanWrite = access.CanWrite,
                    JoinOrder = doc.NextJoinOrder(),
                };
                registry.ClearWarm(doc);
                JoinedDocs().Add(documentId);

                if (!doc.Seeded)
                {
                    if (!doc.SeedInProgress)
                    {
                        // The first writer seeds the doc from the readable file. The relay cannot
                        // validate opaque Yjs state, so allowing a read-only peer to seed would let
                        // it persist arbitrary content and bypass the document ACL.
                        doc.SeedInProgress = true;
                        doc.SnapshotterConnId = Context.ConnectionId;
                        // A host that seeds its documents itself (Atlas 3.6) sees this only as its fallback.
                        log.LogWarning(
                            "Collab join {DocumentId} conn={ConnectionId} user={UserId} — seeding snapshotter (client seed)",
                            documentId, Context.ConnectionId, user.Id);
                        return new JoinResult(null, [], doc.Seq, doc.SeedMarkdown ?? string.Empty, IsSnapshotter: true)
                            { Schema = doc.Schema, CanWrite = true, SeedEditorJson = doc.SeedEditorJson };
                    }

                    // Concurrent joiner: wait for the seed snapshot to land.
                    seedWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    doc.SeedWaiters.Add(seedWaiter);
                }
                else
                {
                    EnsureSnapshotter(doc);
                    var isSnap = doc.SnapshotterConnId == Context.ConnectionId;
                    log.LogInformation(
                        "Collab join {DocumentId} conn={ConnectionId} user={UserId} write={CanWrite} snapshotter={IsSnap} peers={Peers}",
                        documentId, Context.ConnectionId, user.Id, access.CanWrite, isSnap, doc.PeerCount);
                    return Hydrate(doc, isSnap, access.CanWrite);
                }
            }
            finally
            {
                if (doc.PeerCount == 0) registry.MarkWarm(doc, clock.GetUtcNow());
                doc.Gate.Release();
            }

            // ---- seed-wait path (outside the gate) ----
            var winner = await Task.WhenAny(seedWaiter.Task, Task.Delay(options.SeedWait, Context.ConnectionAborted))
                .ConfigureAwait(false);
            var seededOk = winner == seedWaiter.Task && seedWaiter.Task.Result;
            if (!seededOk)
            {
                // Clean up the ghost peer we registered before waiting, then either retry (the seed
                // was abandoned — the elected seeder left → result false) or fail (genuine timeout).
                await LeaveInternalAsync(documentId, connectionGone: false).ConfigureAwait(false);
                if (winner == seedWaiter.Task) continue; // abandoned seed → re-join and re-seed
                throw new HubException($"Timed out waiting for document '{documentId}' to be seeded.");
            }

            await doc.Gate.WaitAsync(Context.ConnectionAborted).ConfigureAwait(false);
            try
            {
                var peer = await accessCoordinator.RequirePeerUnderGateAsync(doc, Context.ConnectionId,
                    write: false, Context.ConnectionAborted).ConfigureAwait(false);
                EnsureSnapshotter(doc);
                var isSnap = doc.SnapshotterConnId == Context.ConnectionId;
                return Hydrate(doc, isSnap, peer.CanWrite);
            }
            finally
            {
                doc.Gate.Release();
            }
        }
    }

    public Task LeaveDocument(string documentId) => LeaveInternalAsync(documentId, connectionGone: false);

    public async Task<long> SendUpdate(string documentId, string clientUpdateId, byte[] update)
    {
        if (!Guid.TryParseExact(clientUpdateId, "D", out var parsedUpdateId) || parsedUpdateId == Guid.Empty)
            throw new HubException("Client update id must be a canonical GUID.");

        var doc = registry.TryGet(documentId) ?? throw new HubException("Not joined to this document.");
        Task? broadcast = null;
        long committedSequence = 0;
        string? passedSnapshotterFrom = null;
        await doc.Gate.WaitAsync(Context.ConnectionAborted).ConfigureAwait(false);
        try
        {
            var peer = await accessCoordinator.RequirePeerUnderGateAsync(doc, Context.ConnectionId,
                write: true, Context.ConnectionAborted).ConfigureAwait(false);
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (peer.UpdateWindow != now) { peer.UpdateWindow = now; peer.UpdateCount = 0; }
            if (++peer.UpdateCount > authorityOptions.MaxUpdatesPerSecond)
                throw new HubException("[RATE_LIMITED] Too many updates from this connection; retry shortly.");
            await authorityCoordinator.EnsureV1UnderGateAsync(doc, Context.ConnectionAborted).ConfigureAwait(false);
            if (!doc.Seeded)
                throw new HubException("The initial snapshot is not durably stored yet.");

            if (doc.TryGetSequence(parsedUpdateId, out var existingSequence))
            {
                if (!doc.TryGetRecentUpdate(parsedUpdateId, out var existing) || existing is null)
                    throw new HubException(
                        "[RETRY_WITH_NEW_ID] This retained update id predates safe replay metadata.");
                if (!existing.Update.AsSpan().SequenceEqual(update))
                    throw new HubException("[UPDATE_ID_COLLISION] Client update id was reused with a different payload.");

                committedSequence = existingSequence;
                // The original durable append may have reached the store while its ordered group
                // broadcast failed. Replaying a Yjs update is idempotent, so a client retry is also
                // the recovery path for delivery after the append log has been compacted as well.
                var previous = doc.BroadcastTail;
                broadcast = BroadcastInOrderAsync(previous, documentId, Context.ConnectionId,
                    existing.Update, existing.Sequence);
                doc.BroadcastTail = broadcast;
            }
            else
            {

                // One update is bounded as in protocol 2; the message keeps "size limit exceeded", which the editor reports as a full document.
                if (update.Length > authorityOptions.MaxUpdateBytes)
                    throw new HubException($"[UPDATE_TOO_LARGE] Update size limit exceeded: one change may hold at most {authorityOptions.MaxUpdateBytes} bytes.");
                if (doc.ApproxStateBytes() + update.Length > options.MaxDocumentBytes)
                    throw new HubException("Document size limit exceeded.");

                if (!doc.ServerClients.IsEmpty && WritesAsServerClient(doc, update))
                    throw InvalidContent(documentId, peer.User, ContentAdmission.Rejected(ContentRejectionReason.Malformed, "structs of a server collaborator's client"));
                // Content admission runs in the same critical section as the durable append it guards.
                var admission = await contentGuard.AdmitUpdateAsync(ContentContext(doc, peer.User), update,
                    Context.ConnectionAborted).ConfigureAwait(false);
                if (!admission.IsAccepted) throw InvalidContent(documentId, peer.User, admission);

                var receivedAtUtc = clock.GetUtcNow();
                AppendUpdateResult appended;
                try
                {
                    // Not cancelled by the connection: a store may commit and then observe the token (NHibernate's CommitAsync does),
                    // which left the update durable but unknown to the live document and every later update refused.
                    appended = await store.AppendUpdateAsync(
                        documentId,
                        parsedUpdateId,
                        peer.User.Id,
                        receivedAtUtc,
                        update,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (DocumentSupersededException)
                {
                    // The intended generation fence, not a failure: the writer rejoins the current document.
                    doc.RenewContentSession();
                    throw new HubException("[SUPERSEDED] The document was replaced; join it again.");
                }
                catch (Exception ex)
                {
                    doc.RenewContentSession();
                    log.LogError(ex, "Durable collaboration update append failed for {DocumentId}", documentId);
                    throw new HubException("Update could not be durably stored.");
                }

                IReadOnlyList<LogUpdate> relay;
                try
                {
                    relay = await registry.CommitDurableAsync(doc, appended, parsedUpdateId, peer.User.Id, receivedAtUtc, update)
                        .ConfigureAwait(false);
                }
                catch
                {
                    doc.RenewContentSession();
                    throw;
                }
                committedSequence = appended.Sequence;
                if (relay.Count > 0) compactor.Consider(doc);
                passedSnapshotterFrom = TakeSnapshotterFromPassivePeer(doc, peer);

                // Queue under the document gate so concurrent writers cannot reorder their sequence
                // numbers. The network send itself runs after the gate is released; slow clients must
                // not block the durability boundary for later updates.
                foreach (var record in relay)
                {
                    var previous = doc.BroadcastTail;
                    // The sender has its own update; a record the live document missed goes to everyone.
                    broadcast = BroadcastInOrderAsync(previous, documentId,
                        record.ClientUpdateId == parsedUpdateId ? Context.ConnectionId : null, record.Update, record.Seq);
                    doc.BroadcastTail = broadcast;
                }
            }
        }
        finally
        {
            doc.Gate.Release();
        }

        if (passedSnapshotterFrom is not null)
        {
            await Clients.Client(passedSnapshotterFrom).SendAsync(SnapshotterChanged, documentId, false).ConfigureAwait(false);
            await Clients.Caller.SendAsync(SnapshotterChanged, documentId, true).ConfigureAwait(false);
        }

        // Preserve the invocation contract expected by callers: once SendUpdate returns, the
        // ordered group broadcast has completed (or logged its recoverable failure).
        if (broadcast is not null) await broadcast.ConfigureAwait(false);
        return committedSequence;
    }

    /// <summary>
    /// The snapshotter renders and uploads the whole page after every batch of changes. A tab only left open paid that for
    /// everyone else's typing, so the first update of a connection takes the role from a snapshotter that has not written.
    /// A snapshotter gone quiet (offline until the client timeout drops it) blocked everyone's save meanwhile, so any writer's
    /// update also takes the role when the oldest update no snapshot covers is older than an online snapshotter lets one wait.
    /// Returns the connection that lost it. Call under the gate.
    /// </summary>
    private string? TakeSnapshotterFromPassivePeer(LiveDocument doc, Peer writer)
    {
        var firstWrite = !writer.HasWritten;
        writer.HasWritten = true;
        if (doc.AuthorityEpoch is not null || doc.SeedInProgress || doc.SnapshotterConnId is not { } current ||
            current == writer.ConnectionId || !doc.Peers.TryGetValue(current, out var owner)) return null;
        var passive = firstWrite && !owner.HasWritten;
        var quiet = doc.Log.Count > 0 && clock.GetUtcNow() - doc.Log[0].ReceivedAtUtc > QuietSnapshotter;
        if (!passive && !quiet) return null;
        doc.SnapshotterConnId = writer.ConnectionId;
        return current;
    }

    /// <summary>Longer than the editor's snapshot schedule lets a change wait (snapshotScheduler.ts: 2 s, then at most 2.5 s for a pause).</summary>
    private static readonly TimeSpan QuietSnapshotter = TimeSpan.FromSeconds(8);

    private async Task BroadcastInOrderAsync(Task previous, string documentId, string? senderConnectionId, byte[] update, long sequence)
    {
        try { await previous.ConfigureAwait(false); }
        catch { /* the previous send logged its own failure; later sequences must still progress */ }
        try
        {
            var doc = registry.TryGet(documentId);
            if (doc is not null)
                await accessCoordinator.SendToReadersAsync(doc, ReceiveUpdate,
                    [documentId, update, sequence], senderConnectionId, expectedProtocolVersion: 1).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Broadcast of durable update {Sequence} failed for {DocumentId}; sender must retry",
                sequence, documentId);
            throw;
        }
    }

    public async Task SendAwareness(string documentId, byte[] payload)
    {
        var doc = registry.TryGet(documentId);
        if (doc is null) return; // best-effort; never stored
        if (payload is null || payload.Length > authorityOptions.MaxAwarenessBytes) return;
        payload = payload.ToArray();
        var protocolVersion = 1;
        await doc.Gate.WaitAsync(Context.ConnectionAborted).ConfigureAwait(false);
        try
        {
            if (!doc.Peers.TryGetValue(Context.ConnectionId, out var peer)) return;
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (peer.AwarenessWindow != now) { peer.AwarenessWindow = now; peer.AwarenessCount = 0; }
            if (++peer.AwarenessCount > authorityOptions.MaxAwarenessPerSecond) return;
            if (doc.AuthorityEpoch is not null)
            {
                protocolVersion = 2;
                if (peer.ProtocolVersion != 2) return;
            }
            // Identity comes from the handshake in both protocols: one owned state, its user rewritten, its client id bound to this connection.
            AuthorityAwareness.Validated validated;
            try { validated = AuthorityAwareness.Validate(payload, peer.User, authorityOptions.MaxAwarenessBytes, presenceOnly: !peer.CanWrite, color: peer.Color); }
            catch (InvalidDataException) { return; }
            if (peer.AwarenessReplaced || peer.AwarenessClientId is { } bound && bound != validated.ClientId) return;
            // A client reconnecting keeps its awareness client id (the provider keeps its Awareness): the same person's old connection, which
            // may linger until its disconnect is noticed, gives it up, so the new one shows at once and the old one's leave removes nothing.
            // Another person's connection never takes it, nor anyone an agent's the server shows.
            var holder = doc.Peers.Values.FirstOrDefault(other => other != peer && other.AwarenessClientId == validated.ClientId);
            if (doc.ServerClients.ContainsKey(validated.ClientId) || holder is not null && holder.User.Id != peer.User.Id) return;
            if (holder is not null) { holder.AwarenessClientId = null; holder.AwarenessReplaced = true; }
            peer.AwarenessClientId = validated.ClientId;
            peer.AwarenessClock = validated.Clock;
            payload = validated.Payload;
        }
        finally { doc.Gate.Release(); }
        await accessCoordinator.SendToReadersAsync(doc, ReceiveAwareness, [documentId, payload],
            Context.ConnectionId, Context.ConnectionAborted, requireSender: true, expectedProtocolVersion: protocolVersion).ConfigureAwait(false);
    }

    /// <summary><see cref="PushSnapshot"/> with the state gzip-compressed (a third of the bytes); inflated within the size limit.</summary>
    public async Task PushSnapshotGzip(string documentId, long basedOnSeq, byte[] compressedState, ProjectionSetDto projections)
    {
        if (compressedState is null) throw new HubException("Snapshot state is missing.");
        AdmitSnapshot(documentId); // before inflating: a reader never makes the server decompress
        byte[] state;
        try
        {
            using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(compressedState), System.IO.Compression.CompressionMode.Decompress);
            using var inflated = new MemoryStream();
            var buffer = new byte[81920];
            for (int read; (read = await gzip.ReadAsync(buffer, Context.ConnectionAborted).ConfigureAwait(false)) > 0;)
            {
                if (inflated.Length + read > options.MaxDocumentBytes) throw new HubException("Snapshot exceeds the document size limit.");
                inflated.Write(buffer, 0, read);
            }
            state = inflated.ToArray();
        }
        catch (InvalidDataException) { throw new HubException("Snapshot state is not valid gzip."); }
        await PushSnapshot(documentId, basedOnSeq, state, projections).ConfigureAwait(false);
    }

    public async Task PushSnapshot(
        string documentId,
        long basedOnSeq,
        byte[] state,
        ProjectionSetDto projections)
    {
        var doc = AdmitSnapshot(documentId);
        if (state is not null && state.Length > options.MaxDocumentBytes)
            throw new HubException("Snapshot exceeds the document size limit.");
        DocSnapshot snapshot;
        try { snapshot = SnapshotValidation.Prepare(new DocSnapshot(state!, basedOnSeq, projections)); }
        catch (InvalidDataException error) { throw new HubException(error.Message); }
        var history = await CaptureHistoryAsync(doc, basedOnSeq).ConfigureAwait(false);
        (snapshot, var shown) = Derive(snapshot, basedOnSeq, history);
        List<TaskCompletionSource<bool>> waiters = [];
        bool isInitialSeed;
        await doc.Gate.WaitAsync(Context.ConnectionAborted).ConfigureAwait(false);
        try
        {
            var snapshotter = await accessCoordinator.RequirePeerUnderGateAsync(doc, Context.ConnectionId,
                write: true, Context.ConnectionAborted).ConfigureAwait(false);
            await authorityCoordinator.EnsureV1UnderGateAsync(doc, Context.ConnectionAborted).ConfigureAwait(false);
            if (doc.SnapshotterConnId != Context.ConnectionId)
                throw new HubException("Only the elected snapshotter may push a snapshot.");
            if (basedOnSeq < 0 || basedOnSeq > doc.Seq)
                throw new HubException("Snapshot sequence is outside the current document history.");
            // The relay compacted past it meanwhile (DocumentCompactor), or it moves nothing: the document already has this baseline.
            if (doc.Seeded && basedOnSeq <= doc.SnapshotSeq) return;
            if (basedOnSeq < doc.SnapshotSeq)
                throw new HubException("Snapshot is older than the current durable baseline.");
            // Joining peers load this state as is, so it is admitted as a whole (the initial seed included).
            var admission = await contentGuard.AdmitSnapshotAsync(ContentContext(doc, snapshotter.User), snapshot.YjsState,
                Context.ConnectionAborted).ConfigureAwait(false);
            if (!admission.IsAccepted) throw InvalidContent(documentId, snapshotter.User, admission);
            // After the guard's verdict, which names the cause more precisely.
            if (!shown.IsAccepted) throw InvalidContent(documentId, snapshotter.User, shown);
            if (doc.Seeded && compactor.Materializer is not null && !ReferenceEquals(history?.Session, doc.ContentSession))
                throw new HubException("The document baseline changed while the snapshot was checked; push it again.");
            isInitialSeed = !doc.Seeded;
            if (!isInitialSeed)
            {
                doc.AdoptSnapshot(snapshot.YjsState, snapshot.Projections, basedOnSeq);
                doc.SeedInProgress = false;
                waiters = [.. doc.SeedWaiters];
                doc.SeedWaiters.Clear();
            }
        }
        finally
        {
            doc.Gate.Release();
        }

        if (isInitialSeed)
        {
            // Durable I/O must not hold the lifecycle gate: disconnect can abandon ownership and
            // concurrent joiners can register their bounded seed wait. The separate persistence
            // gate preserves ordering if ownership changes while an old store call is finishing.
            await doc.SeedPersistenceGate.WaitAsync(Context.ConnectionAborted).ConfigureAwait(false);
            try
            {
                await doc.Gate.WaitAsync(Context.ConnectionAborted).ConfigureAwait(false);
                try
                {
                    await accessCoordinator.RequirePeerUnderGateAsync(doc, Context.ConnectionId,
                        write: true, Context.ConnectionAborted).ConfigureAwait(false);
                    EnsureInitialSeedOwnership(doc, basedOnSeq);
                    doc.SeedPersistenceInFlight = true;
                }
                finally
                {
                    doc.Gate.Release();
                }

                try
                {
                    // A joining peer may only hydrate after this baseline is durable. Cancellation
                    // is safe here: FileDocumentStore reloads after any partial multi-file replace.
                    var seeded = await store.FlushAsync(documentId, SnapshotValidation.Prepare(snapshot), Context.ConnectionAborted).ConfigureAwait(false);
                    if (seeded.IsSuperseded) throw new InvalidOperationException("The store replaced the document before its seed was stored.");
                }
                catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
                {
                    await ReconcileFailedInitialSeedAsync(doc).ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    await ReconcileFailedInitialSeedAsync(doc).ConfigureAwait(false);
                    log.LogError(ex, "Initial collaboration snapshot could not be stored for {DocumentId}", documentId);
                    throw new HubException("Initial snapshot could not be durably stored.");
                }

                // Reconcile with CancellationToken.None after the durability boundary. The
                // persistence gate guarantees that no replacement seed can overtake this result;
                // therefore a completed durable baseline remains authoritative if its owner left.
                await doc.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    doc.SeedPersistenceInFlight = false;
                    doc.SeedPersistenceRequiresReload = false;
                    if (doc.Seeded || doc.Seq != basedOnSeq || doc.SnapshotSeq > basedOnSeq)
                    {
                        // This should be unreachable while SeedPersistenceGate is held. Fail closed
                        // and force the next join to reload the durable store rather than reseeding
                        // from stale in-memory markdown.
                        doc.Loaded = false;
                        doc.ClearSeedingState();
                        throw new HubException("Initial snapshot baseline changed; rejoin the document.");
                    }
                    doc.AdoptSnapshot(snapshot.YjsState, snapshot.Projections, basedOnSeq);
                    doc.SnapshotDirty = false;
                    doc.SeedInProgress = false;
                    doc.SeedMarkdown = null;
                    doc.SeedEditorJson = null;
                    if (doc.SnapshotterConnId == Context.ConnectionId &&
                        (!doc.Peers.TryGetValue(Context.ConnectionId, out var owner) || !owner.CanWrite))
                        doc.SnapshotterConnId = null;
                    EnsureSnapshotter(doc);
                    waiters = [.. doc.SeedWaiters];
                    doc.SeedWaiters.Clear();
                }
                finally
                {
                    doc.Gate.Release();
                }
            }
            finally
            {
                doc.SeedPersistenceGate.Release();
            }
        }

        foreach (var w in waiters) w.TrySetResult(true);
        if (isInitialSeed) await AnnounceSnapshotStoredAsync(accessCoordinator, log, doc, snapshot.CoveredSequence).ConfigureAwait(false);
    }

    /// <summary>The admitted history a seeded document's snapshot must show, captured under the gate and checked off it.</summary>
    private sealed record History(object Session, byte[] Baseline, LogUpdate[] Log);

    private async Task<History?> CaptureHistoryAsync(LiveDocument doc, long basedOnSeq)
    {
        if (compactor.Materializer is null) return null;
        await doc.Gate.WaitAsync(Context.ConnectionAborted).ConfigureAwait(false);
        try
        {
            return doc.Seeded && basedOnSeq > doc.SnapshotSeq ? new History(doc.ContentSession, doc.Snapshot!, [.. doc.Log]) : null;
        }
        finally
        {
            doc.Gate.Release();
        }
    }

    /// <summary>
    /// Off the gate, like the server's compaction: the editor JSON a snapshot stores is what its state shows, never the client's
    /// claim, and with the updates after its sequence it must show what the admitted history shows (no admitted update left out,
    /// nothing deleted that nobody deleted). The verdict is returned, not thrown. An opaque relay keeps the client's projection.
    /// </summary>
    private (DocSnapshot Snapshot, ContentAdmission Shown) Derive(DocSnapshot snapshot, long basedOnSeq, History? history)
    {
        if (compactor.Materializer is not { } materializer) return (snapshot, ContentAdmission.Accepted);
        try
        {
            var json = materializer.Materialize(snapshot.YjsState);
            var shown = ContentAdmission.Accepted;
            if (history is not null)
            {
                var after = history.Log.Where(record => record.Seq > basedOnSeq).Select(record => (ReadOnlyMemory<byte>)record.Update).ToArray();
                var withAfter = after.Length == 0 ? json : materializer.Materialize(snapshot.YjsState, after);
                if (withAfter != materializer.Materialize(history.Baseline, history.Log.Select(record => (ReadOnlyMemory<byte>)record.Update)))
                    shown = ContentAdmission.Rejected(ContentRejectionReason.SnapshotMissesHistory, $"snapshot at seq {basedOnSeq}");
            }
            using var editorJson = JsonDocument.Parse(json);
            return (snapshot with { Projections = snapshot.Projections with { EditorJson = editorJson.RootElement.Clone() } }, shown);
        }
        catch (YjsMaterializationException error)
        {
            return (snapshot, ContentAdmission.Rejected(error.Reason, error.Detail));
        }
    }

    private void EnsureInitialSeedOwnership(LiveDocument doc, long basedOnSeq)
    {
        if (doc.Seeded || !doc.SeedInProgress || doc.SnapshotterConnId != Context.ConnectionId ||
            !doc.Peers.TryGetValue(Context.ConnectionId, out var peer) || !peer.CanWrite)
            throw new HubException("Initial snapshot ownership changed; rejoin the document.");
        if (doc.Seq != basedOnSeq || doc.SnapshotSeq > basedOnSeq)
            throw new HubException("Initial snapshot baseline changed; rejoin the document.");
    }

    private async Task ReconcileFailedInitialSeedAsync(LiveDocument doc)
    {
        await doc.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            doc.SeedPersistenceInFlight = false;
            doc.SeedPersistenceRequiresReload = true;
            if (doc.SeedInProgress && doc.SnapshotterConnId == Context.ConnectionId &&
                (!doc.Peers.TryGetValue(Context.ConnectionId, out var peer) || !peer.CanWrite))
            {
                doc.Loaded = false;
                doc.ClearSeedingState();
            }
        }
        finally
        {
            doc.Gate.Release();
        }
    }

    // ---- internals ----

    private static JoinResult Hydrate(LiveDocument doc, bool isSnapshotter, bool canWrite)
        => new(doc.Snapshot, doc.Log.Select(r => r.Update).ToArray(), doc.Seq, null, isSnapshotter)
            { Schema = doc.Schema, CanWrite = canWrite };

    /// <summary>Elect the earliest-joined writer as snapshotter if there is none (or it left). Call under the gate.</summary>
    private static void EnsureSnapshotter(LiveDocument doc)
    {
        if (doc.AuthorityEpoch is not null) { doc.SnapshotterConnId = null; return; }
        if (doc.SnapshotterConnId is not null &&
            doc.Peers.TryGetValue(doc.SnapshotterConnId, out var owner) && owner.CanWrite) return;
        doc.SnapshotterConnId = doc.WriterPeersByAge().FirstOrDefault()?.ConnectionId;
    }

    private async Task LeaveInternalAsync(string documentId, bool connectionGone)
    {
        // Revocation may already have removed the peer. Connection-local cleanup is idempotent.
        JoinedDocs().Remove(documentId);
        if (!connectionGone) accessCoordinator.RemoveGroupMembership(Context.ConnectionId, documentId);
        var doc = registry.TryGet(documentId);
        if (doc is null) return;

        string? newSnapshotter = null;
        var lastLeft = false;
        Peer? left;
        await doc.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!doc.Peers.TryRemove(Context.ConnectionId, out left)) return;

            if (doc.SeedInProgress && doc.SnapshotterConnId == Context.ConnectionId)
            {
                if (doc.SeedPersistenceInFlight)
                {
                    // Keep the ownership marker until the store call resolves. Joiners may wait,
                    // but none may become a replacement seeder and overwrite a result that has
                    // already crossed the durability boundary.
                    log.LogInformation(
                        "Collab doc {DocumentId} seeding snapshotter left during persistence — awaiting durable outcome",
                        documentId);
                }
                else
                {
                    log.LogInformation(
                        "Collab doc {DocumentId} seeding snapshotter left before persistence — abandoning seed",
                        documentId);
                    if (doc.SeedPersistenceRequiresReload) doc.Loaded = false;
                    doc.ClearSeedingState();
                }
            }
            else if (doc.SnapshotterConnId == Context.ConnectionId)
            {
                doc.SnapshotterConnId = null;
                var next = doc.WriterPeersByAge().FirstOrDefault();
                if (next is not null)
                {
                    doc.SnapshotterConnId = next.ConnectionId;
                    newSnapshotter = next.ConnectionId;
                }
            }

            if (doc.PeerCount == 0)
            {
                registry.MarkWarm(doc, clock.GetUtcNow());
                log.LogInformation("Collab doc {DocumentId} now warm (0 peers)", documentId);
                lastLeft = doc.Seeded;
            }
        }
        finally
        {
            doc.Gate.Release();
        }

        // The client's own removal is best-effort and often lost: sent as its connection stops, it is cancelled with it (COL2-13).
        // Without this the others kept its avatar, without a caret, until their 30 s timeout: one more per page visit it ended.
        if (left?.AwarenessClientId is { } awarenessClient)
        {
            try
            {
                await accessCoordinator.SendToReadersAsync(doc, ReceiveAwareness,
                    [documentId, AuthorityAwareness.Removal(awarenessClient, left.AwarenessClock + 1)]).ConfigureAwait(false);
            }
            catch (Exception error) { log.LogInformation(error, "Collab doc {DocumentId}: a leaving peer's presence could not be removed", documentId); }
        }

        if (lastLeft)
        {
            // Outside the gate: the host's checkpoint takes it itself.
            try { await store.LastPeerLeftAsync(documentId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { log.LogWarning(error, "Collab doc {DocumentId}: the host could not take its last peer's leave", documentId); }
        }

        if (newSnapshotter is not null)
        {
            log.LogInformation("Collab doc {DocumentId} re-elected snapshotter {ConnectionId}", documentId,
                newSnapshotter);
            await Clients.Client(newSnapshotter).SendAsync(SnapshotterChanged, documentId, true).ConfigureAwait(false);
            await Clients.Client(newSnapshotter).SendAsync(RequestSnapshot, documentId).ConfigureAwait(false);
        }
    }

    private static DocumentContentContext ContentContext(LiveDocument doc, CollabUser author)
        => new(doc.DocumentId, author.Id,
            doc.Peers.Values.Select(peer => peer.User.Id).Distinct(StringComparer.Ordinal).ToArray(),
            doc.Schema, doc.Snapshot, doc.Log, doc.ContentSession);

    /// <summary>The update writes as a collaborator the server shows (an agent's presence). A payload that does not decode is the content guard's to refuse.</summary>
    private static bool WritesAsServerClient(LiveDocument doc, byte[] update)
    {
        try { return YjsUpdateDecoder.Decode(update, long.MaxValue).Clients.Any(range => doc.ServerClients.ContainsKey((long)range.Client)); }
        catch (Exception) { return false; }
    }

    private HubException InvalidContent(string documentId, CollabUser author, ContentAdmission admission)
    {
        log.LogWarning("Rejected collaborative content for {DocumentId} from {UserId}: {Reason} {Detail}",
            documentId, author.Id, admission.Reason, admission.Detail);
        return new HubException($"[INVALID_CONTENT] The document does not accept this content ({admission.Reason}).");
    }

    /// <summary>Cheap admission of a snapshot before copying, hashing or inflating it; current authorization is checked again under the gate.</summary>
    private LiveDocument AdmitSnapshot(string documentId)
    {
        var doc = registry.TryGet(documentId) ?? throw new HubException("Not joined to this document.");
        if (!doc.Peers.TryGetValue(Context.ConnectionId, out var peer))
            throw new HubException("Not joined to this document.");
        if (!peer.CanWrite)
            throw new HubException("[READ_ONLY] This document is read-only.");
        if (doc.SnapshotterConnId != Context.ConnectionId)
            throw new HubException("Only the elected snapshotter may push a snapshot.");
        return doc;
    }

    private CollabUser RequireUser()
        => Context.Items.TryGetValue(UserKey, out var u) && u is CollabUser user
            ? user
            : throw new HubException("Unauthenticated.");

    private HashSet<string> JoinedDocs()
        => Context.Items.TryGetValue(JoinedKey, out var j) && j is HashSet<string> set
            ? set
            : new HashSet<string>(StringComparer.Ordinal);
}
