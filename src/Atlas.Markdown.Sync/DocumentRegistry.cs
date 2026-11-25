using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Process-wide registry of live collaborative documents (ADR-002 single
/// instance → an in-memory dictionary is sufficient; no backplane). Documents
/// are created lazily on first join, kept warm for <see cref="MarkdownSyncOptions.WarmKeep"/>
/// after the last peer leaves, and evicted by the flush service's sweep.
/// </summary>
public sealed class DocumentRegistry(
    IDocumentStore store,
    MarkdownSyncOptions options,
    CollaboratorColors colors,
    ILogger<DocumentRegistry> log)
{
    private readonly ConcurrentDictionary<string, LiveDocument> _docs = new(StringComparer.Ordinal);

    /// <summary>Number of documents currently held (including warm-kept ones with zero peers).</summary>
    public int Count => _docs.Count;

    /// <summary>Number of documents with at least one connected peer.</summary>
    public int ActiveDocumentCount => _docs.Values.Count(d => d.PeerCount > 0);

    /// <summary>Total connected collaborators across every document (drives the connections gauge).</summary>
    public int TotalPeerCount => _docs.Values.Sum(d => d.PeerCount);

    /// <summary>Get the live document shell, creating an empty one if absent. Callers load/mutate under its gate.</summary>
    internal LiveDocument GetOrCreate(string documentId)
        => _docs.GetOrAdd(documentId, static id => new LiveDocument(id));

    /// <summary>Gives a peer joining <paramref name="doc"/> its colour, from the scope the document shares with the host's other live
    /// surfaces (<see cref="MarkdownSyncOptions.ColorScope"/>); it keeps it until it leaves or restamps. Call under the gate.</summary>
    internal void AssignColor(LiveDocument doc, string connectionId, CollabUser user)
        => colors.Assign(options.ColorScope(doc.DocumentId), ColorHolder(doc, connectionId), user.Id, user.Color);

    /// <summary>The colour the peer holds now (<see cref="CollaboratorColors.Restamp"/> may have changed it since its join).</summary>
    internal string? ColorOf(LiveDocument doc, string connectionId)
        => colors.Find(options.ColorScope(doc.DocumentId), ColorHolder(doc, connectionId));

    /// <summary>The peer left <paramref name="doc"/>: its colour is free again. Call under the gate, wherever a peer is removed.</summary>
    internal void ReleaseColor(LiveDocument doc, string connectionId)
        => colors.Release(options.ColorScope(doc.DocumentId), ColorHolder(doc, connectionId));

    // One connection may hold two generations of a page (one scope) for a moment; each releases only its own.
    private static string ColorHolder(LiveDocument doc, string connectionId) => connectionId + " " + doc.DocumentId;

    internal LiveDocument? TryGet(string documentId)
        => _docs.TryGetValue(documentId, out var doc) ? doc : null;

    internal IReadOnlyList<LiveDocument> All() => _docs.Values.ToArray();

    /// <summary>
    /// One-time cold load of persisted state. MUST be called with <paramref name="doc"/>.Gate held.
    /// On external-change (readable file drifted underneath stored state) the stale state is
    /// discarded and only the seed markdown is kept → the caller takes the reseed path.
    /// </summary>
    internal async Task EnsureLoadedAsync(LiveDocument doc, CancellationToken ct)
    {
        if (doc.Loaded) return;
        var stored = await store.LoadAsync(doc.DocumentId, ct).ConfigureAwait(false);
        if (stored is not null)
        {
            if (stored.ExternalChange)
            {
                // The readable file was edited while no session was live → adopt it as the seed
                // (external wins when nobody's editing). Mark the follow-up flush's drift as an
                // expected adoption, not a live-session conflict, so it doesn't fire a misleading
                // "external edit archived" toast (ADR-018 §5).
                doc.SuppressNextExternalEditReport = true;
                log.LogInformation("Collab doc {DocumentId} loaded with external change — discarding stale state, will reseed", doc.DocumentId);
            }
            else
            {
                doc.HydrateFrom(stored);
            }
            doc.SeedMarkdown = stored.SeedMarkdown;
            doc.SeedEditorJson = stored.SeedEditorJson?.Clone();
        }
        doc.Loaded = true;
    }

    /// <summary>
    /// Reflects a durable append in <paramref name="doc"/> and returns the records its peers still need, in sequence order. When the
    /// durable log is ahead of the live document (an earlier append committed although its caller saw a failure), the document reloads
    /// it here instead of refusing every later update: the missed records come back with this one. MUST be called with the gate held.
    /// </summary>
    internal async Task<IReadOnlyList<LogUpdate>> CommitDurableAsync(LiveDocument doc, AppendUpdateResult appended, Guid clientUpdateId,
        string userId, DateTimeOffset receivedAtUtc, byte[] update)
    {
        if (appended.Sequence <= doc.Seq + 1)
            return doc.CommitDurableUpdate(appended, clientUpdateId, userId, receivedAtUtc, update) ? [doc.Log[^1]] : [];
        var behind = doc.Seq;
        var stored = await store.LoadAsync(doc.DocumentId, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidDataException("The durable log of a live document is missing.");
        doc.HydrateFrom(stored);
        log.LogWarning("Collab doc {DocumentId} was behind its durable log at {Behind}; reloaded it to {Head}", doc.DocumentId, behind, doc.Seq);
        return doc.Log.Where(record => record.Seq > behind).ToArray();
    }

    /// <summary>
    /// Re-consult the store for an external edit that landed while a warm-kept document had zero
    /// peers (no flush tick runs then). On mismatch the stale in-memory state is reset in place and
    /// reloaded so the next join cold-reseeds — object identity in the registry is preserved so a
    /// concurrent joiner can't split-brain onto a fresh shell. MUST be called with the gate held.
    /// Returns true if a reseed happened.
    /// </summary>
    internal async Task<bool> ReseedIfExternallyChangedWarmAsync(LiveDocument doc, CancellationToken ct)
    {
        if (doc.PeerCount > 0) return false; // live session wins; handled on the flush tick, not here
        if (!doc.Seeded) return false;
        if (!await store.HasExternalChangeAsync(doc.DocumentId, ct).ConfigureAwait(false)) return false;

        log.LogInformation("Collab doc {DocumentId} changed externally while warm-kept with no peers — resetting for cold reseed", doc.DocumentId);
        doc.ResetForReseed();
        await EnsureLoadedAsync(doc, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Mark a document warm (last peer left) at <paramref name="now"/>. Call under the gate.</summary>
    internal void MarkWarm(LiveDocument doc, DateTimeOffset now) => doc.WarmSince = now;

    /// <summary>Clear warm state (a peer joined). Call under the gate.</summary>
    internal void ClearWarm(LiveDocument doc) => doc.WarmSince = null;

    /// <summary>Documents whose warm-keep window elapsed at <paramref name="now"/> and are ready to evict.</summary>
    internal IReadOnlyList<LiveDocument> DueForEviction(DateTimeOffset now)
        => _docs.Values
            .Where(d => IsDueForEviction(d, now))
            .ToArray();

    /// <summary>Re-check the current warm lease under the document gate before removal.</summary>
    internal bool IsDueForEviction(LiveDocument doc, DateTimeOffset now)
        => doc.PeerCount == 0 && doc.WarmSince is { } since && now - since >= options.WarmKeep;

    /// <summary>Remove a document from the registry. Call after a final flush.</summary>
    internal void Evict(string documentId) => _docs.TryRemove(documentId, out _);

    /// <summary>
    /// Remove <paramref name="doc"/> only if it is still the registered instance for its id
    /// (compare-and-remove). Prevents evicting a document a concurrent join already re-registered.
    /// Returns true if this exact instance was removed.
    /// </summary>
    internal bool EvictIfCurrent(LiveDocument doc)
        => ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, LiveDocument>>)_docs)
            .Remove(new System.Collections.Generic.KeyValuePair<string, LiveDocument>(doc.DocumentId, doc));

    /// <summary>The currently-registered instance for an id (may differ from a stale reference).</summary>
    internal LiveDocument? Current(string documentId)
        => _docs.TryGetValue(documentId, out var d) ? d : null;
}
