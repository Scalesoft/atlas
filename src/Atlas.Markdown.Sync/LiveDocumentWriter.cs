using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Atlas.Markdown.Sync;

/// <summary>Where a host-written update came from (3.6-H attribution): the channel, the personal API token, the assistant conversation.</summary>
public sealed record UpdateOrigin(string Channel, Guid? ApiTokenId = null, Guid? ConversationId = null);

/// <summary>
/// A host's write into a live document as one more collaborator (3.6-H). <see cref="HoldAsync"/> takes the document gate, so nothing
/// else changes the document until the hold is disposed; the host reads the live state, may store a checkpoint of it and appends its
/// update through the same content guard, durable append and ordered broadcast as a browser's. Lock order as the hub's: the document
/// gate first, then the store's own transactions. Never take a hold inside a database transaction that locks the page: the hub holds
/// the gate while it waits for those rows (AppendUpdate), which is a deadlock the database cannot see.
/// </summary>
public sealed class LiveDocumentWriter(MarkdownSyncOptions options, DocumentRegistry registry, IDocumentStore store,
    DocumentAccessCoordinator access, DocumentAuthorityCoordinator authority, IDocumentContentGuard guard, DocumentCompactor compactor, TimeProvider clock,
    ILogger<LiveDocumentWriter> log)
{
    /// <returns>The held document, or null when it has no live state yet (never seeded): the host writes as it did before.</returns>
    public async Task<LiveDocumentHold?> HoldAsync(string documentId, CancellationToken ct)
    {
        while (true)
        {
            var doc = registry.GetOrCreate(documentId);
            await doc.Gate.WaitAsync(ct).ConfigureAwait(false);
            var held = false;
            try
            {
                if (!ReferenceEquals(registry.Current(documentId), doc)) continue; // evicted underneath us, as JoinCoreAsync
                await authority.EnsureV1UnderGateAsync(doc, ct).ConfigureAwait(false);
                await registry.EnsureLoadedAsync(doc, ct).ConfigureAwait(false);
                if (!doc.Seeded)
                {
                    if (doc.SeedInProgress) throw new InvalidOperationException("[RETRY] The live document is being seeded.");
                    return null;
                }
                held = true;
                return new LiveDocumentHold(this, doc);
            }
            finally
            {
                if (!held) Release(doc);
            }
        }
    }

    /// <summary>
    /// The host's seed of a live document nobody has seeded (3.6): under the document gate, so it is the one seed — a writer's join waits
    /// for it and then hydrates, and a writer already seeding (or a seeded document) wins and nothing is built. The host builds the
    /// snapshot at sequence 0 for <paramref name="schema"/>, which is bound first as a seeding writer's join binds it (a seed that fails
    /// leaves the document unbound), the store's flush stores it as a pushed initial snapshot would be, and the next join loads it.
    /// Same lock order as <see cref="HoldAsync"/>: never inside a database transaction that locks the page.
    /// </summary>
    /// <returns>True when this call stored the seed; false when it was seeded, being seeded, or the store moved to another generation.</returns>
    public async Task<bool> SeedAsync(string documentId, MarkdownDocumentSchema schema, Func<CancellationToken, Task<DocSnapshot>> build, CancellationToken ct)
    {
        while (true)
        {
            var doc = registry.GetOrCreate(documentId);
            await doc.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(registry.Current(documentId), doc)) continue; // evicted underneath us, as JoinCoreAsync
                await authority.EnsureV1UnderGateAsync(doc, ct).ConfigureAwait(false);
                await registry.EnsureLoadedAsync(doc, ct).ConfigureAwait(false);
                if (doc.Seeded || doc.SeedInProgress || doc.Seq != 0) return false;
                var snapshot = await build(ct).ConfigureAwait(false);
                DocumentSchemaCompatibility.EnsureCompatible(await store.BindSchemaAsync(documentId, schema, ct).ConfigureAwait(false), schema);
                if ((await store.FlushAsync(documentId, snapshot, ct).ConfigureAwait(false)).IsSuperseded) return false;
                doc.Loaded = false; // the next join or hold loads the stored seed
                return true;
            }
            finally
            {
                Release(doc);
            }
        }
    }

    /// <summary>Whether anyone is connected to the document now (a hint read without the gate).</summary>
    public bool HasPeers(string documentId) => registry.TryGet(documentId)?.PeerCount > 0;

    /// <summary>
    /// Shows a collaborator that is no connection (an agent writing for a person) to everyone on the document, as an awareness state
    /// of its own client id, or removes it (<paramref name="user"/> null). Best effort, never stored; call it without a hold.
    /// </summary>
    /// <param name="caret">Where its caret stands (<see cref="Atlas.Yjs.YjsCaret"/>): the editors draw it as a person's, as y-prosemirror's
    /// cursor state, a collapsed selection of two relative positions right after that unit.</param>
    public async Task ShowAsync(string documentId, long clientId, int clock, CollabUser? user, CancellationToken ct = default,
        (ulong Client, uint Clock)? caret = null)
    {
        if (registry.TryGet(documentId) is not { } doc) return;
        var shown = user is null ? null : new { id = user.Id, name = user.Name, color = user.Color ?? "#7c3aed" };
        var at = caret is { } c ? new { item = new { client = c.Client, clock = c.Clock }, assoc = -1 } : null;
        var state = shown is null ? "null"
            : JsonSerializer.Serialize(at is null ? new { user = shown } : (object)new { user = shown, cursor = new { anchor = at, head = at } }, Unescaped);
        var json = Encoding.UTF8.GetBytes(state);
        using var payload = new MemoryStream();
        foreach (var value in new[] { 1L, clientId, clock, json.Length }) WriteVarUint(payload, value);
        payload.Write(json);
        try
        {
            await access.SendToReadersAsync(doc, MarkdownSyncHub.ReceiveAwareness, [documentId, payload.ToArray()], ct: ct,
                expectedProtocolVersion: 1).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogInformation(error, "Presence of a server collaborator could not be sent for {DocumentId}", documentId);
        }
    }

    // Awareness is JSON parsed by the editors, never HTML: names keep their letters (Czech, the "·" of an agent's label).
    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void WriteVarUint(Stream stream, long value)
    {
        while (value > 127) { stream.WriteByte((byte)((value & 127) | 128)); value >>= 7; }
        stream.WriteByte((byte)value);
    }

    internal async Task<long> AppendAsync(LiveDocument doc, CollabUser author, byte[] update, UpdateOrigin origin, CancellationToken ct)
    {
        if (doc.ApproxStateBytes() + update.Length > options.MaxDocumentBytes) throw new InvalidDataException("[DOCUMENT_FULL] Document size limit exceeded.");
        var context = new DocumentContentContext(doc.DocumentId, author.Id,
            doc.Peers.Values.Select(peer => peer.User.Id).Distinct(StringComparer.Ordinal).ToArray(), doc.Schema, doc.Snapshot, doc.Log, doc.ContentSession);
        var admission = await guard.AdmitUpdateAsync(context, update, ct).ConfigureAwait(false);
        if (!admission.IsAccepted)
            throw new InvalidDataException($"[INVALID_CONTENT] The document does not accept this content ({admission.Reason}).");
        var updateId = Guid.NewGuid();
        var receivedAt = clock.GetUtcNow();
        long sequence;
        IReadOnlyList<LogUpdate> relay;
        try
        {
            // Not cancellable, as the hub's: a store may commit and then observe the token.
            var appended = await store.AppendUpdateAsync(doc.DocumentId, updateId, author.Id, receivedAt, update, origin, CancellationToken.None).ConfigureAwait(false);
            relay = await registry.CommitDurableAsync(doc, appended, updateId, author.Id, receivedAt, update).ConfigureAwait(false);
            sequence = appended.Sequence;
        }
        catch
        {
            doc.RenewContentSession(); // the guard recorded the update; rebuild its baseline from durable state
            throw;
        }
        compactor.Consider(doc);
        foreach (var record in relay)
        {
            var previous = doc.BroadcastTail;
            doc.BroadcastTail = BroadcastAsync(previous, doc, record.Update, record.Seq);
        }
        return sequence;
    }

    internal async Task AdoptAsync(LiveDocument doc, DocSnapshot snapshot, CancellationToken ct)
    {
        doc.AdoptSnapshot(snapshot.YjsState, snapshot.Projections, snapshot.CoveredSequence);
        await store.FlushAsync(doc.DocumentId, snapshot, ct).ConfigureAwait(false);
        doc.SnapshotDirty = false;
        // Readers learn the head is stored (a colleague's waiting save goes through), once the hold is released.
        var previous = doc.BroadcastTail;
        doc.BroadcastTail = AnnounceAsync(previous, doc, snapshot.CoveredSequence);
    }

    private async Task AnnounceAsync(Task previous, LiveDocument doc, long sequence)
    {
        try { await previous.ConfigureAwait(false); }
        catch { /* the previous send logged its own failure */ }
        await MarkdownSyncHub.AnnounceSnapshotStoredAsync(access, log, doc, sequence).ConfigureAwait(false);
    }

    internal void Release(LiveDocument doc)
    {
        if (doc.PeerCount == 0) registry.MarkWarm(doc, clock.GetUtcNow());
        doc.Gate.Release();
    }

    private async Task BroadcastAsync(Task previous, LiveDocument doc, byte[] update, long sequence)
    {
        try { await previous.ConfigureAwait(false); }
        catch { /* the previous send logged its own failure */ }
        try
        {
            // Waits for the gate, so it runs once the hold is released.
            await access.SendToReadersAsync(doc, MarkdownSyncHub.ReceiveUpdate, [doc.DocumentId, update, sequence],
                expectedProtocolVersion: 1).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Durable already: peers catch up from the log on their next join or resync.
            log.LogWarning(error, "Broadcast of server update {Sequence} failed for {DocumentId}", sequence, doc.DocumentId);
        }
    }
}

/// <summary>A live document held under its gate by <see cref="LiveDocumentWriter.HoldAsync"/>; disposing releases it.</summary>
public sealed class LiveDocumentHold : IAsyncDisposable
{
    private readonly LiveDocumentWriter m_writer;
    private readonly LiveDocument m_doc;
    private bool m_released;

    internal LiveDocumentHold(LiveDocumentWriter writer, LiveDocument doc)
    {
        m_writer = writer;
        m_doc = doc;
    }

    public string DocumentId => m_doc.DocumentId;
    public MarkdownDocumentSchema? Schema => m_doc.Schema;
    public byte[] Snapshot => m_doc.Snapshot ?? [];
    public IReadOnlyList<byte[]> Updates => m_doc.Log.Select(record => record.Update).ToArray();
    public long Sequence => m_doc.Seq;
    public int PeerCount => m_doc.PeerCount;

    /// <summary>The author of the newest update after <paramref name="sequence"/>, if any: whose edits a checkpoint at the head saves.</summary>
    public string? AuthorAfter(long sequence) => m_doc.Log.LastOrDefault(record => record.Seq > sequence)?.UserId;

    /// <summary>Stores <paramref name="snapshot"/> as the durable state of the head (the store's flush), as a pushed snapshot would be.</summary>
    public Task AdoptAsync(DocSnapshot snapshot, CancellationToken ct) => m_writer.AdoptAsync(m_doc, snapshot, ct);

    /// <summary>Admits, appends and relays one update of <paramref name="author"/>; its sequence. The peers get it once the hold is released.</summary>
    public Task<long> AppendAsync(CollabUser author, byte[] update, UpdateOrigin origin, CancellationToken ct)
        => m_writer.AppendAsync(m_doc, author, update, origin, ct);

    public async ValueTask DisposeAsync()
    {
        if (m_released) return;
        m_released = true;
        var broadcast = m_doc.BroadcastTail;
        m_writer.Release(m_doc);
        try { await broadcast.ConfigureAwait(false); } catch { /* logged by the broadcast */ }
    }
}
