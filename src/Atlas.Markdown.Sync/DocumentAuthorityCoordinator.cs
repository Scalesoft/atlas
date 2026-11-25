using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Markdown.Sync;

/// <summary>Single-instance V2 cutover boundary. Hosts must explicitly approve migration of existing history.</summary>
public sealed class DocumentAuthorityCoordinator(DocumentRegistry registry, IDocumentStore store,
    IServiceProvider services, IHubContext<MarkdownSyncHub> hub, DocumentAuthorityOptions options)
{
    internal bool Supported => store is IDocumentAuthorityStore && services.GetService<IDocumentAuthorityReducer>() is not null;
    internal IDocumentAuthorityStore AuthorityStore => store as IDocumentAuthorityStore
        ?? throw new HubException("[PROTOCOL_UNSUPPORTED] Store does not support authoritative V2.");
    internal IDocumentAuthorityReducer Reducer => services.GetService<IDocumentAuthorityReducer>()
        ?? throw new HubException("[AUTHORITY_UNAVAILABLE] Server reducer is not configured.");

    internal async Task EnsureV1UnderGateAsync(LiveDocument doc, CancellationToken ct)
    {
        if (doc.AuthorityEpoch is not null || store is IDocumentAuthorityStore capable
            && await capable.LoadAuthorityAsync(doc.DocumentId, ct).ConfigureAwait(false) is not null)
            throw new HubException("[PROTOCOL_V2_REQUIRED] This document permanently requires protocol V2.");
    }

    internal async Task<DocumentAuthorityState> LoadOrBootstrapUnderGateAsync(LiveDocument doc,
        MarkdownDocumentSchema schema, bool canWrite, CancellationToken ct)
    {
        var state = await AuthorityStore.LoadAuthorityAsync(doc.DocumentId, ct).ConfigureAwait(false);
        if (state is null)
        {
            if (!canWrite) throw new HubException("[READ_ONLY] A writer is required to initialize V2 authority.");
            var legacy = await store.LoadAsync(doc.DocumentId, ct).ConfigureAwait(false);
            if (legacy?.Snapshot is not null || legacy?.Updates.Count > 0 || legacy?.Seq > 0 || doc.SeedInProgress)
                throw new HubException("[MIGRATION_REQUIRED] Existing history requires explicit host-authorized V2 cutover.");
            await store.BindSchemaAsync(doc.DocumentId, schema, ct).ConfigureAwait(false);
            var baseline = await Reducer.ReduceAsync(schema, options.Fragment, null, [], legacy?.SeedMarkdown ?? "", 0, ct).ConfigureAwait(false);
            state = await AuthorityStore.ActivateAuthorityAsync(doc.DocumentId, schema, options.Fragment, 0, baseline, ct).ConfigureAwait(false);
            FencePeersUnderGate(doc);
        }
        DocumentSchemaCompatibility.EnsureCompatible(state.Schema, schema);
        PublishUnderGate(doc, state);
        return state;
    }

    /// <summary>
    /// Explicit operator cutover after backup/review. The expected hash identifies the reviewed
    /// reducer output; it does not prove completeness of history compacted by earlier V1 clients.
    /// </summary>
    public async Task<DocumentAuthorityState> ActivateAsync(string documentId, MarkdownDocumentSchema schema,
        long expectedSequence, string expectedStateSha256, CancellationToken ct = default)
    {
        documentId = DocumentId.Parse(documentId).Value;
        schema = DocumentSchemaCompatibility.Normalize(schema);
        var doc = registry.GetOrCreate(documentId);
        await doc.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(registry.Current(documentId), doc)) throw new InvalidOperationException("Document was evicted; retry cutover.");
            var active = await AuthorityStore.LoadAuthorityAsync(documentId, ct).ConfigureAwait(false);
            if (active is not null) return active;
            if (doc.SeedInProgress || doc.SeedPersistenceInFlight) throw new InvalidOperationException("A V1 initial seed is still in progress.");
            var legacy = await store.LoadAsync(documentId, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("Document does not exist.");
            DocumentSchemaCompatibility.EnsureCompatible(legacy.Schema, schema);
            if (legacy.Seq != expectedSequence) throw new InvalidDataException("[STALE_BASELINE] Reviewed history has changed.");
            var baseline = await Reducer.ReduceAsync(schema, options.Fragment, legacy.Snapshot,
                legacy.Updates.Select(update => update.Update).ToArray(), legacy.Snapshot is null ? legacy.SeedMarkdown ?? "" : null,
                expectedSequence, ct).ConfigureAwait(false);
            if (!string.Equals(baseline.Projections.StateSha256, expectedStateSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("[STALE_BASELINE] Reviewed authoritative state hash differs.");
            var activated = await AuthorityStore.ActivateAuthorityAsync(documentId, schema, options.Fragment,
                expectedSequence, baseline, ct).ConfigureAwait(false);
            FencePeersUnderGate(doc);
            PublishUnderGate(doc, activated);
            return activated;
        }
        finally { doc.Gate.Release(); }
    }

    internal async Task<DocumentAuthorityState> CompactIfNeededUnderGateAsync(DocumentAuthorityState state, CancellationToken ct)
    {
        if (store is not IDocumentAuthorityCompactionStore compactable) return state;
        var retained = state.Records.Where(record => record.Receipt.Sequence > state.Baseline.CoveredSequence).ToArray();
        if (retained.Length < 128 && retained.Sum(record => (long)record.Update.Length) < 4L * 1024 * 1024) return state;
        var verified = await Reducer.ReduceAsync(state.Schema, state.Fragment, state.Baseline.YjsState,
            retained.Select(record => record.Update).ToArray(), null, state.Sequence, ct).ConfigureAwait(false);
        // Store checks exact state/projection equivalence again under its own persistence gate.
        return await compactable.CompactAuthorityAsync(state.DocumentId, state.Epoch, verified, ct).ConfigureAwait(false);
    }
    private void FencePeersUnderGate(LiveDocument doc)
    {
        var peers = doc.Peers.Values.ToArray();
        doc.Peers.Clear();
        doc.ClearSeedingState();
        foreach (var peer in peers)
        {
            registry.ReleaseColor(doc, peer.ConnectionId);
            // ACL is not changed; existing V1 sessions must reconnect through V2 before writing.
            try { _ = ObserveAsync(hub.Clients.Client(peer.ConnectionId).SendCoreAsync(
                "DocumentAccessChanged", [doc.DocumentId, false, false])); }
            catch { /* the durable fence and removed membership already deny subsequent use */ }
        }
    }

    private static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch { /* fence is enforced by method/store admission */ } }

    internal static void PublishUnderGate(LiveDocument doc, DocumentAuthorityState state)
    {
        doc.HydrateFrom(new StoredDocument(state.Snapshot.YjsState, [], state.Sequence, null, false,
            new Dictionary<Guid, long>()) { Schema = state.Schema });
        doc.AuthorityEpoch = state.Epoch;
        doc.Loaded = true;
        doc.SnapshotterConnId = null;
        doc.SnapshotDirty = false;
    }
}

