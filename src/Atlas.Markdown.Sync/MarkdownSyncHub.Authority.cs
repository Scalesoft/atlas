using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;

namespace Atlas.Markdown.Sync;

public sealed partial class MarkdownSyncHub
{
    public Task<AuthorityCapabilities> GetCapabilities(string documentId) => AuthorityErrors(async () =>
    {
        documentId = DocumentId.Parse(documentId).Value;
        var access = await accessCoordinator.AuthorizeAsync(RequireUser(), documentId, Context.ConnectionAborted).ConfigureAwait(false);
        if (!access.CanRead) throw new HubException("[FORBIDDEN] Document access denied.");
        if (!authorityCoordinator.Supported) return new AuthorityCapabilities(1, false, "client", "manual-only", 0);
        var active = await authorityCoordinator.AuthorityStore.LoadAuthorityAsync(documentId, Context.ConnectionAborted).ConfigureAwait(false);
        return new AuthorityCapabilities(2, active is not null, "server", "durable-receipts", authorityOptions.MaxUpdateBytes);
    });

    public Task<AuthorityJoinResult> JoinDocumentV2(string documentId, MarkdownDocumentSchema schema) => AuthorityErrors(async () =>
    {
        documentId = DocumentId.Parse(documentId).Value;
        schema = DocumentSchemaCompatibility.Normalize(schema);
        var user = RequireUser();
        var access = await accessCoordinator.AuthorizeAsync(user, documentId, Context.ConnectionAborted).ConfigureAwait(false);
        if (!access.CanRead) throw new HubException("[FORBIDDEN] Document access denied.");
        while (true)
        {
            var doc = registry.GetOrCreate(documentId);
            await doc.Gate.WaitAsync(Context.ConnectionAborted).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(registry.Current(documentId), doc)) continue;
                access = await accessCoordinator.AuthorizeAsync(user, documentId, Context.ConnectionAborted).ConfigureAwait(false);
                if (!access.CanRead) throw new HubException("[FORBIDDEN] Document access denied.");
                var state = await authorityCoordinator.LoadOrBootstrapUnderGateAsync(doc, schema, access.CanWrite,
                    Context.ConnectionAborted).ConfigureAwait(false);
                access = await accessCoordinator.AuthorizeAsync(user, documentId, Context.ConnectionAborted).ConfigureAwait(false);
                if (!access.CanRead) throw new HubException("[FORBIDDEN] Document access denied.");
                doc.Peers[Context.ConnectionId] = new Peer { ConnectionId = Context.ConnectionId, User = user, Color = doc.ColorFor(user),
                    CanWrite = access.CanWrite, JoinOrder = doc.NextJoinOrder(), ProtocolVersion = 2 };
                JoinedDocs().Add(documentId);
                registry.ClearWarm(doc);
                return new AuthorityJoinResult(2, state.Epoch, state.Schema, state.Fragment, state.Snapshot.YjsState.ToArray(),
                    state.Sequence, state.Sequence, state.Sequence, access.CanWrite,
                    new AuthorityLimits(authorityOptions.MaxUpdateBytes, options.MaxDocumentBytes));
            }
            finally
            {
                if (doc.PeerCount == 0) registry.MarkWarm(doc, clock.GetUtcNow());
                doc.Gate.Release();
            }
        }
    });

    public Task<DocumentUpdateReceipt> SendUpdateV2(string documentId, AuthorityUpdateRequest request) => AuthorityErrors(async () =>
    {
        if (request is null || request.Update is null || request.Update.Length == 0 || request.Update.Length > authorityOptions.MaxUpdateBytes
            || !Guid.TryParseExact(request.UpdateId, "D", out var updateId) || updateId == Guid.Empty
            || request.Sha256 is not { Length: 64 }) throw new HubException("[INVALID_UPDATE] Invalid update envelope.");
        var update = request.Update.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(update)).ToLowerInvariant();
        if (!string.Equals(hash, request.Sha256, StringComparison.OrdinalIgnoreCase)) throw new HubException("[INVALID_UPDATE] Update hash differs.");
        var doc = registry.TryGet(documentId) ?? throw new HubException("[FORBIDDEN] Not joined to this document.");
        Task? broadcast = null;
        DocumentUpdateReceipt receipt;
        await doc.Gate.WaitAsync(Context.ConnectionAborted).ConfigureAwait(false);
        try
        {
            var peer = await accessCoordinator.RequirePeerUnderGateAsync(doc, Context.ConnectionId, true, Context.ConnectionAborted).ConfigureAwait(false);
            if (peer.ProtocolVersion != 2) throw new HubException("[PROTOCOL_V2_REQUIRED] Join with V2 before writing.");
            var state = await authorityCoordinator.AuthorityStore.LoadAuthorityAsync(documentId, Context.ConnectionAborted).ConfigureAwait(false)
                ?? throw new HubException("[AUTHORITY_UNAVAILABLE] V2 document state is unavailable.");
            if (request.Epoch != state.Epoch) throw new HubException("[EPOCH_MISMATCH] Document recovery epoch differs.");
            var previous = state.Records.FirstOrDefault(record => record.Receipt.UpdateId == updateId.ToString("D"));
            if (previous is not null && (!string.Equals(previous.Receipt.Sha256, hash, StringComparison.OrdinalIgnoreCase)
                || previous.Update.Length > 0 && !previous.Update.AsSpan().SequenceEqual(update)))
                throw new HubException("[UPDATE_ID_COLLISION] Update identity aliases another payload.");
            DocSnapshot candidate;
            if (previous is not null) candidate = state.Snapshot;
            else
            {
                state = await authorityCoordinator.CompactIfNeededUnderGateAsync(state, Context.ConnectionAborted).ConfigureAwait(false);
                if (state.Sequence >= 9_007_199_254_740_990) throw new HubException("[DOCUMENT_FULL] Sequence budget exhausted.");
                candidate = await authorityCoordinator.Reducer.ReduceAsync(state.Schema, state.Fragment,
                    state.Snapshot.YjsState, [update], null, state.Sequence + 1, Context.ConnectionAborted).ConfigureAwait(false);
                if (candidate.YjsState.Length > options.MaxDocumentBytes) throw new HubException("[DOCUMENT_FULL] Authoritative state exceeds its byte budget.");
                // External reducer work may outlive a host ACL change. Reauthorize before durable admission.
                await accessCoordinator.RequirePeerUnderGateAsync(doc, Context.ConnectionId, true, Context.ConnectionAborted).ConfigureAwait(false);
            }
            receipt = await authorityCoordinator.AuthorityStore.CommitAuthorityAsync(documentId, state.Epoch, state.Sequence,
                updateId, hash, peer.User.Id, clock.GetUtcNow(), update, SnapshotValidation.Prepare(candidate), Context.ConnectionAborted).ConfigureAwait(false);
            if (previous is null) DocumentAuthorityCoordinator.PublishUnderGate(doc, state with { Snapshot = candidate });
            var delivery = new AuthorityUpdateDelivery(state.Epoch, receipt.UpdateId, receipt.Sha256, update,
                receipt.Sequence, receipt.PersistedSequence);
            broadcast = BroadcastAuthorityAsync(doc.BroadcastTail, doc, delivery, Context.ConnectionId);
            doc.BroadcastTail = broadcast;
        }
        finally { doc.Gate.Release(); }
        if (broadcast is not null) await broadcast.ConfigureAwait(false);
        return receipt;
    });

    public Task<DocumentUpdateReceipt?> GetUpdateReceiptV2(string documentId, string epoch, string updateId) => AuthorityErrors(async () =>
    {
        if (!Guid.TryParseExact(updateId, "D", out var parsedId) || parsedId == Guid.Empty) throw new HubException("[INVALID_UPDATE] Invalid update id.");
        var doc = registry.TryGet(documentId) ?? throw new HubException("[FORBIDDEN] Not joined to this document.");
        await doc.Gate.WaitAsync(Context.ConnectionAborted).ConfigureAwait(false);
        try
        {
            var peer = await accessCoordinator.RequirePeerUnderGateAsync(doc, Context.ConnectionId, false, Context.ConnectionAborted).ConfigureAwait(false);
            if (peer.ProtocolVersion != 2) throw new HubException("[PROTOCOL_V2_REQUIRED] Join with V2 first.");
            var state = await authorityCoordinator.AuthorityStore.LoadAuthorityAsync(documentId, Context.ConnectionAborted).ConfigureAwait(false)
                ?? throw new HubException("[AUTHORITY_UNAVAILABLE] V2 document state unavailable.");
            if (state.Epoch != epoch) throw new HubException("[EPOCH_MISMATCH] Document recovery epoch differs.");
            return state.Records.FirstOrDefault(record => record.Receipt.UpdateId == parsedId.ToString("D"))?.Receipt;
        }
        finally { doc.Gate.Release(); }
    });

    private async Task BroadcastAuthorityAsync(Task previous, LiveDocument doc, AuthorityUpdateDelivery delivery, string sender)
    {
        try { await previous.ConfigureAwait(false); } catch { /* later durable deliveries continue */ }
        await accessCoordinator.SendToReadersAsync(doc, "ReceiveUpdateV2", [doc.DocumentId, delivery], sender, expectedProtocolVersion: 2).ConfigureAwait(false);
    }

    private static async Task<T> AuthorityErrors<T>(Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (InvalidDataException error) { throw new HubException(error.Message); }
        catch (DocumentSchemaException error) { throw new HubException(error.Message); }
    }
}

