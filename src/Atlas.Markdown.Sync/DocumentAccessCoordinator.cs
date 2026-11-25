using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Revalidates live document permissions. Collaboration traffic (updates, snapshots, awareness and
/// their broadcasts) never authorizes: it uses each peer's access as last authorized, which a join,
/// the periodic refresh (<see cref="MarkdownSyncOptions.AccessRefreshInterval"/>) and the host's
/// <see cref="RefreshAsync"/> / <see cref="RefreshAllAsync"/> update. Hosts await those after committing
/// an ACL change for an explicit revocation boundary; otherwise a revocation takes effect at the next
/// periodic refresh. A refresh looks access up outside the document gate, so a slow lookup never stalls
/// the document's traffic, and applies the results under it; all admission is serialized by the gate.
/// Messages already handed to SignalR cannot be recalled.
/// </summary>
public sealed class DocumentAccessCoordinator(
    DocumentRegistry registry,
    IServiceScopeFactory scopes,
    IHubContext<MarkdownSyncHub> hub,
    MarkdownSyncOptions options,
    TimeProvider clock,
    ILogger<DocumentAccessCoordinator> log)
{
    internal const string AccessChanged = "DocumentAccessChanged";
    private readonly SemaphoreSlim authorizationSlots = new(options.MaxConcurrentAuthorizations);
    private long revalidations;

    /// <summary>Counts the revalidations begun (<see cref="RefreshAsync"/>, <see cref="RefreshAllAsync"/>, the periodic refresh).
    /// A host revalidates after committing an ACL change, so a join that sees no new one since it authorized may keep that
    /// result (<see cref="CurrentAsync"/>); a revalidation begun later reads the peers under the gate after the join's and finds
    /// its peer. A peer keeps the newest revalidation applied to it (<see cref="Peer.AccessRevalidation"/>), so a lookup that
    /// finishes late never overwrites a newer one.</summary>
    internal long Revalidations => Interlocked.Read(ref revalidations);

    /// <summary>The access one join authorized at <paramref name="revalidation"/>, authorized again only when a revalidation
    /// began since. The caller keeps the result for that join only, never for another join or connection.</summary>
    internal async Task<(DocAccess Access, long Revalidation)> CurrentAsync(CollabUser user, string documentId, DocAccess access,
        long revalidation, CancellationToken ct)
    {
        var now = Revalidations;
        return now == revalidation ? (access, revalidation) : (await AuthorizeAsync(user, documentId, ct).ConfigureAwait(false), now);
    }

    /// <summary>Revalidates every live document, for ACL changes that are not tied to one document (roles, groups, accounts).</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var revalidation = Interlocked.Increment(ref revalidations);
        foreach (var doc in registry.All())
            await RefreshDocumentAsync(doc, revalidation, ct).ConfigureAwait(false);
    }

    /// <summary>Revalidates the read-only peers of the live documents <paramref name="documents"/> selects, for a host change that ends
    /// those documents without changing anyone's access (a document replaced by a newer one). A reader never writes, so nothing else would
    /// tell it; writers keep learning it from their next write.</summary>
    public async Task RefreshReadersAsync(Func<string, bool> documents, CancellationToken ct = default)
    {
        var revalidation = Interlocked.Increment(ref revalidations);
        foreach (var doc in registry.All().Where(doc => documents(doc.DocumentId)))
            await RefreshDocumentAsync(doc, revalidation, ct, readersOnly: true).ConfigureAwait(false);
    }

    public async Task RefreshAsync(string documentId, CancellationToken ct = default)
    {
        if (!DocumentId.TryParse(documentId, out var parsed))
            throw new ArgumentException("Document id is invalid.", nameof(documentId));
        var revalidation = Interlocked.Increment(ref revalidations);
        var doc = registry.TryGet(parsed!.Value);
        if (doc is null) return;
        await RefreshDocumentAsync(doc, revalidation, ct).ConfigureAwait(false);
    }

    // A fresh scope permits the host's authenticator to use scoped ACL/database services.
    // Timeout cancels cooperative implementations and stops waiting for non-cooperative ones;
    // their late result has no continuation that can grant access.
    internal async Task<DocAccess> AuthorizeAsync(CollabUser user, string documentId, CancellationToken ct)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.AuthorizationTimeout);
        AsyncServiceScope? scope = null;
        Task<DocAccess>? authorization = null;
        var admitted = false;
        try
        {
            await authorizationSlots.WaitAsync(deadline.Token).ConfigureAwait(false);
            admitted = true;
            scope = scopes.CreateAsyncScope();
            authorization = scope.Value.ServiceProvider.GetRequiredService<ICollabAuthenticator>()
                .AuthorizeAsync(user, documentId, deadline.Token);
            var result = await authorization.WaitAsync(deadline.Token).ConfigureAwait(false);
            return result?.Level is DocAccessLevel.Read or DocAccessLevel.Write ? result : DocAccess.Denied;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            log.LogWarning(error, "Document authorization unavailable for {DocumentId}; access denied", documentId);
            return DocAccess.Denied;
        }
        finally { _ = ReleaseAuthorizationAsync(authorization, scope, deadline, admitted); }
    }

    private async Task ReleaseAuthorizationAsync(Task<DocAccess>? authorization, AsyncServiceScope? scope,
        CancellationTokenSource deadline, bool admitted)
    {
        // Scope disposal must neither race the authenticator nor hold the document gate.
        // A non-cooperative lookup keeps one bounded slot until it really completes.
        await Task.Yield();
        try
        {
            if (authorization is not null)
                try { await authorization.ConfigureAwait(false); } catch { /* already denied/observed */ }
            if (scope is { } ownedScope) await ownedScope.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) { log.LogWarning(error, "Authorization scope cleanup failed"); }
        finally
        {
            deadline.Dispose();
            if (admitted) authorizationSlots.Release();
        }
    }

    // Collaboration requests use the access last authorized for the peer: a refresh that denies read
    // removes the peer, one that denies write clears CanWrite. No lookup happens on this path.
    internal Task<Peer> RequirePeerUnderGateAsync(LiveDocument doc, string connectionId,
        bool write, CancellationToken ct)
    {
        var peer = doc.Peers.GetValueOrDefault(connectionId)
            ?? throw new HubException("[FORBIDDEN] Not joined to this document or its access was revoked.");
        if (write && !peer.CanWrite) throw new HubException("[READ_ONLY] Document writes are not accepted.");
        return Task.FromResult(peer);
    }

    // The peers are read under the gate after the revalidation began: a join either finished before (its peer is here) or checks
    // CurrentAsync after it. The lookups run without the gate; a result is applied only to the same peer, and only when no
    // lookup begun later was applied to it already.
    private async Task RefreshDocumentAsync(LiveDocument doc, long revalidation, CancellationToken ct, bool readersOnly = false)
    {
        Peer[] peers;
        await doc.Gate.WaitAsync(ct).ConfigureAwait(false);
        try { peers = doc.Peers.Values.Where(peer => !readersOnly || !peer.CanWrite).ToArray(); }
        finally { doc.Gate.Release(); }
        var results = await Task.WhenAll(peers.Select(peer => AuthorizeAsync(peer.User, doc.DocumentId, ct)))
            .ConfigureAwait(false);
        await doc.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var index = 0; index < peers.Length; index++)
            {
                var peer = peers[index];
                if (!doc.Peers.TryGetValue(peer.ConnectionId, out var current) || !ReferenceEquals(current, peer)
                    || peer.AccessRevalidation >= revalidation) continue;
                peer.AccessRevalidation = revalidation;
                ApplyUnderGate(doc, peer, results[index]);
            }
        }
        finally { doc.Gate.Release(); }
    }

    private void ApplyUnderGate(LiveDocument doc, Peer peer, DocAccess access)
    {
        if (access.CanRead && access.CanWrite == peer.CanWrite) return;
        peer.CanWrite = access.CanWrite;
        if (!access.CanRead)
        {
            if (doc.Peers.TryRemove(peer.ConnectionId, out _)) registry.ReleaseColor(doc, peer.ConnectionId);
            // Group membership is cleanup only. Every publication selects authorized IDs.
            RemoveGroupMembership(peer.ConnectionId, doc.DocumentId);
        }
        Control(peer.ConnectionId, AccessChanged, doc.DocumentId, access.CanRead, access.CanWrite);

        if (doc.SnapshotterConnId == peer.ConnectionId && !access.CanWrite)
        {
            Control(peer.ConnectionId, MarkdownSyncHub.SnapshotterChanged, doc.DocumentId, false);
            if (doc.SeedInProgress)
            {
                if (!doc.SeedPersistenceInFlight)
                {
                    if (doc.SeedPersistenceRequiresReload) doc.Loaded = false;
                    doc.ClearSeedingState();
                }
                // An already admitted seed write keeps its ownership until durable reconciliation.
            }
            else
            {
                doc.SnapshotterConnId = null;
                ElectUnderGate(doc);
            }
        }
        if (doc.Seeded && doc.SnapshotterConnId is null) ElectUnderGate(doc);
        if (doc.PeerCount == 0) registry.MarkWarm(doc, clock.GetUtcNow());
    }

    internal void ElectUnderGate(LiveDocument doc)
    {
        if (doc.AuthorityEpoch is not null) { doc.SnapshotterConnId = null; return; }
        if (doc.SnapshotterConnId is { } current &&
            doc.Peers.TryGetValue(current, out var owner) && owner.CanWrite) return;
        doc.SnapshotterConnId = doc.WriterPeersByAge().FirstOrDefault()?.ConnectionId;
        if (doc.SnapshotterConnId is { } next)
        {
            Control(next, MarkdownSyncHub.SnapshotterChanged, doc.DocumentId, true);
            Control(next, MarkdownSyncHub.RequestSnapshot, doc.DocumentId);
        }
    }

    private void Control(string connectionId, string method, params object?[] args)
        => Observe(() => hub.Clients.Client(connectionId).SendCoreAsync(method, args));

    internal void RemoveGroupMembership(string connectionId, string documentId)
        => Observe(() => hub.Groups.RemoveFromGroupAsync(connectionId, MarkdownSyncHub.GroupName(documentId)));

    private void Observe(Func<Task> send)
    {
        try { _ = ObserveAsync(send()); }
        catch (Exception error) { log.LogWarning(error, "Access control notification could not be sent"); }
    }

    private async Task ObserveAsync(Task send)
    {
        try { await send.ConfigureAwait(false); }
        catch (Exception error) { log.LogWarning(error, "Access control notification could not be sent"); }
    }

    // Select recipients under the gate, await the transport outside it. Recipients are the peers
    // authorized at the last refresh; a broadcast never looks permissions up. Callers must wait for
    // their ordered BroadcastTail BEFORE calling this method.
    internal async Task SendToReadersAsync(LiveDocument doc, string method, object?[] args,
        string? excludedConnectionId = null, CancellationToken ct = default, bool requireSender = false,
        int? expectedProtocolVersion = null)
    {
        Task send;
        await doc.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (expectedProtocolVersion is { } protocol && (doc.AuthorityEpoch is null ? 1 : 2) != protocol) return;
            if (requireSender && (excludedConnectionId is null || !doc.Peers.ContainsKey(excludedConnectionId))) return;
            var recipients = doc.Peers.Keys.Where(id => id != excludedConnectionId).ToArray();
            send = hub.Clients.Clients(recipients).SendCoreAsync(method, args, ct);
        }
        finally { doc.Gate.Release(); }
        await send.ConfigureAwait(false);
    }
}

internal sealed class DocumentAccessRefreshService(
    DocumentRegistry registry, DocumentAccessCoordinator access, MarkdownSyncOptions options,
    ILogger<DocumentAccessRefreshService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.AccessRefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            foreach (var doc in registry.All())
            {
                try { await access.RefreshAsync(doc.DocumentId, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) { log.LogWarning(error, "Live document access refresh failed"); }
            }
        }
    }
}
