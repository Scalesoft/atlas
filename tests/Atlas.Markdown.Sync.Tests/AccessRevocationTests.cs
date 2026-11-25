using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Atlas.Markdown.Sync;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>Real-socket authorization races; refresh completion is the revocation boundary.</summary>
public class AccessRevocationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string Doc = "access-document";

    private static Task<HubTestHost> StartAsync(MutableAuthenticator auth, IDocumentStore? store = null)
        => HubTestHost.StartAsync(options => options.AccessRefreshInterval = TimeSpan.FromHours(1),
            store: store, authenticator: auth);

    private static ProjectionSetDto Projection(byte[] state)
    {
        using var json = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[]}");
        return new ProjectionSetDto(0, Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant(),
            "seed", json.RootElement.Clone(), "seed");
    }

    private static Task PushSeedAsync(HubConnection connection, string documentId = Doc)
    {
        byte[] state = [0];
        return connection.InvokeAsync("PushSnapshot", documentId, 0L, state, Projection(state));
    }

    private static async Task SeedAsync(HubConnection connection, string documentId = Doc)
    {
        Assert.True((await connection.InvokeAsync<JoinResult>("JoinDocument", documentId)).IsSnapshotter);
        await PushSeedAsync(connection, documentId);
    }

    private static Task<long> SendAsync(HubConnection connection, string documentId = Doc)
        => connection.InvokeAsync<long>("SendUpdate", documentId, Guid.NewGuid().ToString("D"), new byte[] { 1, 2 });

    private static async Task WaitForPeerAsync(HubTestHost host, string connectionId)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        while (true)
        {
            var doc = host.Registry.TryGet(Doc)!;
            await doc.Gate.WaitAsync(deadline.Token);
            try { if (doc.Peers.ContainsKey(connectionId) && doc.SeedWaiters.Count > 0) return; }
            finally { doc.Gate.Release(); }
            await Task.Delay(10, deadline.Token);
        }
    }

    [Fact]
    public async Task Idle_background_refresh_revokes_access_without_client_activity()
    {
        var auth = new MutableAuthenticator();
        await using var host = await HubTestHost.StartAsync(
            options => options.AccessRefreshInterval = TimeSpan.FromMilliseconds(25), authenticator: auth);
        await using var alice = host.Connection("alice");
        await alice.StartAsync();
        await SeedAsync(alice);
        var changed = new TaskCompletionSource<(bool Read, bool Write)>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.On<string, bool, bool>("DocumentAccessChanged", (id, read, write) =>
        {
            if (id == Doc) changed.TrySetResult((read, write));
        });

        auth.Set("alice", Doc, DocAccess.Denied);
        // No RefreshAsync, hub invocation or write occurs between the ACL mutation and
        // this notification. Only the hosted timer can discover the idle revocation.
        Assert.Equal((false, false), await changed.Task.WaitAsync(Timeout));

        var live = host.Registry.TryGet(Doc)!;
        using var deadline = new CancellationTokenSource(Timeout);
        await live.Gate.WaitAsync(deadline.Token);
        try
        {
            Assert.DoesNotContain(alice.ConnectionId!, live.Peers.Keys);
            Assert.Null(live.SnapshotterConnId);
            Assert.Equal(0, live.Seq);
        }
        finally { live.Gate.Release(); }
        Assert.Equal(0, host.Store.AppendCallsOf(Doc));
    }

    [Fact]
    public async Task Refresh_grants_live_reader_write_and_fresh_join_reports_effective_permission()
    {
        var auth = new MutableAuthenticator();
        auth.Set("bob", Doc, DocAccess.ReadOnly);
        await using var host = await StartAsync(auth);
        await using var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await alice.StartAsync();
        await bob.StartAsync();
        await SeedAsync(alice);
        var originalJoin = await bob.InvokeAsync<JoinResult>("JoinDocument", Doc).WaitAsync(Timeout);
        Assert.False(originalJoin.CanWrite);
        await Assert.ThrowsAsync<HubException>(() => SendAsync(bob).WaitAsync(Timeout));
        var changed = new TaskCompletionSource<(bool Read, bool Write)>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.On<string, bool, bool>("DocumentAccessChanged", (id, read, write) =>
        {
            if (id == Doc) changed.TrySetResult((read, write));
        });
        var received = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.On<string, byte[], long>("ReceiveUpdate", (id, _, seq) =>
        {
            if (id == Doc) received.TrySetResult(seq);
        });

        auth.Set("bob", Doc, DocAccess.Write);
        await host.Access.RefreshAsync(Doc).WaitAsync(Timeout);
        Assert.Equal((true, true), await changed.Task.WaitAsync(Timeout));
        Assert.True(host.Registry.TryGet(Doc)!.Peers[bob.ConnectionId!].CanWrite);
        var refreshedJoin = await bob.InvokeAsync<JoinResult>("JoinDocument", Doc).WaitAsync(Timeout);
        Assert.True(refreshedJoin.CanWrite);
        Assert.Equal(1, await SendAsync(bob).WaitAsync(Timeout));
        Assert.Equal(1, await received.Task.WaitAsync(Timeout));
        Assert.Equal(1, host.Store.AppendCallsOf(Doc));
    }

    [Fact]
    public async Task Refresh_of_readers_revalidates_only_the_read_only_peers_of_the_selected_documents()
    {
        const string other = "other-document";
        var auth = new MutableAuthenticator();
        auth.Set("bob", Doc, DocAccess.ReadOnly);
        auth.Set("bob", other, DocAccess.ReadOnly);
        await using var host = await StartAsync(auth);
        await using var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await alice.StartAsync(); await bob.StartAsync();
        await SeedAsync(alice);
        await SeedAsync(alice, other);
        await bob.InvokeAsync<JoinResult>("JoinDocument", Doc).WaitAsync(Timeout);
        await bob.InvokeAsync<JoinResult>("JoinDocument", other).WaitAsync(Timeout);
        var changed = new ConcurrentQueue<string>();
        alice.On<string, bool, bool>("DocumentAccessChanged", (id, _, _) => changed.Enqueue("alice " + id));
        bob.On<string, bool, bool>("DocumentAccessChanged", (id, _, _) => changed.Enqueue("bob " + id));

        foreach (var user in new[] { "alice", "bob" })
            foreach (var document in new[] { Doc, other }) auth.Set(user, document, DocAccess.Denied);
        await host.Access.RefreshReadersAsync(id => id == Doc).WaitAsync(Timeout);

        Assert.Equal([alice.ConnectionId!], host.Registry.TryGet(Doc)!.Peers.Keys);
        Assert.Contains(bob.ConnectionId!, host.Registry.TryGet(other)!.Peers.Keys);
        using var deadline = new CancellationTokenSource(Timeout);
        while (changed.IsEmpty) await Task.Delay(10, deadline.Token);
        await Task.Delay(100, deadline.Token);
        Assert.Equal(["bob " + Doc], changed.ToArray());
    }

    [Fact]
    public async Task Refresh_downgrades_writer_preserves_read_and_reelects_snapshotter()
    {
        var auth = new MutableAuthenticator();
        await using var host = await StartAsync(auth);
        await using var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await alice.StartAsync(); await bob.StartAsync();
        await SeedAsync(alice);
        await bob.InvokeAsync<JoinResult>("JoinDocument", Doc);
        var changed = new TaskCompletionSource<(bool Read, bool Write)>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.On<string, bool, bool>("DocumentAccessChanged", (_, read, write) => changed.TrySetResult((read, write)));
        var received = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.On<string, byte[], long>("ReceiveUpdate", (_, _, seq) => received.TrySetResult(seq));

        auth.Set("alice", Doc, DocAccess.ReadOnly);
        await host.Access.RefreshAsync(Doc).WaitAsync(Timeout);

        Assert.Equal((true, false), await changed.Task.WaitAsync(Timeout));
        var live = host.Registry.TryGet(Doc)!;
        Assert.False(live.Peers[alice.ConnectionId!].CanWrite);
        Assert.Equal(bob.ConnectionId, live.SnapshotterConnId);
        await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(alice));
        await Assert.ThrowsAnyAsync<Exception>(() => PushSeedAsync(alice));
        Assert.Equal(1, await SendAsync(bob));
        Assert.Equal(1, await received.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Refresh_denies_both_sockets_without_affecting_another_document()
    {
        var auth = new MutableAuthenticator();
        await using var host = await StartAsync(auth);
        await using var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await using var bobSecond = host.Connection("bob");
        await using var witness = host.Connection("witness");
        await alice.StartAsync(); await bob.StartAsync(); await bobSecond.StartAsync(); await witness.StartAsync();
        await SeedAsync(alice); await SeedAsync(alice, "other-document");
        foreach (var connection in new[] { bob, bobSecond, witness }) await connection.InvokeAsync<JoinResult>("JoinDocument", Doc);
        await bob.InvokeAsync<JoinResult>("JoinDocument", "other-document");
        var leaked = new ConcurrentQueue<string>();
        foreach (var connection in new[] { bob, bobSecond })
        {
            connection.On<string, byte[], long>("ReceiveUpdate", (id, _, _) => { if (id == Doc) leaked.Enqueue("update"); });
            connection.On<string, byte[]>("ReceiveAwareness", (id, _) => { if (id == Doc) leaked.Enqueue("awareness"); });
        }
        var witnessUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var witnessPresence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        witness.On<string, byte[], long>("ReceiveUpdate", (_, _, _) => witnessUpdate.TrySetResult());
        witness.On<string, byte[]>("ReceiveAwareness", (_, _) => witnessPresence.TrySetResult());
        var other = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.On<string, byte[], long>("ReceiveUpdate", (id, _, _) => { if (id == "other-document") other.TrySetResult(); });

        auth.Set("bob", Doc, DocAccess.Denied);
        await host.Access.RefreshAsync(Doc).WaitAsync(Timeout);
        Assert.DoesNotContain(host.Registry.TryGet(Doc)!.Peers.Values, peer => peer.User.Id == "bob");
        await SendAsync(alice);
        await alice.InvokeAsync("SendAwareness", Doc, AuthorityAwarenessTests.Frame("{}"));
        await witnessUpdate.Task.WaitAsync(Timeout); await witnessPresence.Task.WaitAsync(Timeout);
        // Round trips on both revoked sockets also establish an incoming-message barrier.
        foreach (var connection in new[] { bob, bobSecond })
            await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync<JoinResult>("JoinDocument", Doc));
        Assert.Empty(leaked);
        await SendAsync(alice, "other-document");
        await other.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Collaboration_traffic_uses_the_last_authorized_access_until_the_next_refresh()
    {
        var auth = new MutableAuthenticator();
        await using var host = await StartAsync(auth);
        await using var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await alice.StartAsync(); await bob.StartAsync(); await SeedAsync(alice);
        await bob.InvokeAsync<JoinResult>("JoinDocument", Doc);
        var lookups = auth.Lookups;
        auth.Set("alice", Doc, DocAccess.ReadOnly);

        // Updates, awareness and their broadcasts never look permissions up (owner decision 2026-09-26).
        Assert.Equal(1, await SendAsync(alice));
        await alice.InvokeAsync("SendAwareness", Doc, AuthorityAwarenessTests.Frame("{}"));
        await bob.InvokeAsync("SendAwareness", Doc, AuthorityAwarenessTests.Frame("{}", client: 43));
        Assert.Equal(2, await SendAsync(alice));
        Assert.Equal(lookups, auth.Lookups);

        // The refresh is the revocation boundary.
        await host.Access.RefreshAsync(Doc).WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(alice));
        await Assert.ThrowsAnyAsync<Exception>(() => PushSeedAsync(alice));
        Assert.Equal(2, host.Registry.TryGet(Doc)!.Seq);
        Assert.Equal(2, host.Store.AppendCallsOf(Doc));
    }

    [Fact]
    public async Task Refresh_all_revalidates_every_live_document()
    {
        var auth = new MutableAuthenticator();
        await using var host = await StartAsync(auth);
        await using var alice = host.Connection("alice");
        await alice.StartAsync(); await SeedAsync(alice); await SeedAsync(alice, "second-document");
        auth.Set("alice", Doc, DocAccess.ReadOnly);
        auth.Set("alice", "second-document", DocAccess.Denied);

        await host.Access.RefreshAllAsync().WaitAsync(Timeout);

        Assert.False(host.Registry.TryGet(Doc)!.Peers[alice.ConnectionId!].CanWrite);
        Assert.DoesNotContain(alice.ConnectionId!, host.Registry.TryGet("second-document")!.Peers.Keys);
        await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(alice));
        await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(alice, "second-document"));
    }

    [Fact]
    public async Task Revoked_waiting_joiner_does_not_hydrate_after_seed_finishes()
    {
        var auth = new MutableAuthenticator();
        await using var host = await StartAsync(auth);
        await using var seeder = host.Connection("seeder");
        await using var waiting = host.Connection("waiting");
        await seeder.StartAsync(); await waiting.StartAsync();
        await seeder.InvokeAsync<JoinResult>("JoinDocument", Doc);
        var join = waiting.InvokeAsync<JoinResult>("JoinDocument", Doc);
        await WaitForPeerAsync(host, waiting.ConnectionId!);
        auth.Set("waiting", Doc, DocAccess.Denied);
        await host.Access.RefreshAsync(Doc).WaitAsync(Timeout);
        await PushSeedAsync(seeder);

        await Assert.ThrowsAsync<HubException>(() => join.WaitAsync(Timeout));
        Assert.DoesNotContain(waiting.ConnectionId!, host.Registry.TryGet(Doc)!.Peers.Keys);
    }

    [Fact]
    public async Task Downgraded_seeder_cannot_seed_and_waiting_writer_can_take_over()
    {
        var auth = new MutableAuthenticator();
        await using var host = await StartAsync(auth);
        await using var seeder = host.Connection("seeder");
        await using var replacement = host.Connection("replacement");
        await seeder.StartAsync(); await replacement.StartAsync();
        await seeder.InvokeAsync<JoinResult>("JoinDocument", Doc);
        var join = replacement.InvokeAsync<JoinResult>("JoinDocument", Doc);
        await WaitForPeerAsync(host, replacement.ConnectionId!);
        auth.Set("seeder", Doc, DocAccess.ReadOnly);
        await host.Access.RefreshAsync(Doc).WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<Exception>(() => PushSeedAsync(seeder));
        var next = await join.WaitAsync(Timeout);
        Assert.True(next.IsSnapshotter);
        Assert.True(next.CanWrite);
        await PushSeedAsync(replacement);
        Assert.True(host.Registry.TryGet(Doc)!.Seeded);
    }

    [Fact]
    public async Task Delayed_authorization_cannot_restore_write_after_refresh_completes()
    {
        var auth = new MutableAuthenticator();
        await using var host = await StartAsync(auth);
        await using var alice = host.Connection("alice");
        await using var late = host.Connection("late");
        await alice.StartAsync(); await late.StartAsync(); await SeedAsync(alice);
        var pause = auth.BlockNext("late", Doc);
        var join = late.InvokeAsync<JoinResult>("JoinDocument", Doc);
        await pause.Entered.Task.WaitAsync(Timeout);
        auth.Set("late", Doc, DocAccess.Denied);
        var refresh = host.Access.RefreshAsync(Doc);
        // An already authorized operation may finish before refresh acquires the gate.
        pause.Release.TrySetResult();
        try { await join.WaitAsync(Timeout); } catch (HubException) { /* fresh recheck may already reject it */ }
        await refresh.WaitAsync(Timeout);

        await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(late));
        await Assert.ThrowsAnyAsync<Exception>(() => late.InvokeAsync<JoinResult>("JoinDocument", Doc));
        Assert.DoesNotContain(late.ConnectionId!, host.Registry.TryGet(Doc)!.Peers.Keys);
        Assert.Equal(0, host.Registry.TryGet(Doc)!.Seq);
    }

    [Fact]
    public async Task Seeder_downgrade_after_durable_seed_does_not_discard_baseline_or_restore_write()
    {
        var auth = new MutableAuthenticator();
        var store = new PersistedThenBlockingInitialFlushStore();
        await using var host = await StartAsync(auth, store);
        await using var seeder = host.Connection("seeder");
        await using var replacement = host.Connection("replacement");
        await seeder.StartAsync(); await replacement.StartAsync();
        await seeder.InvokeAsync<JoinResult>("JoinDocument", Doc);
        var join = replacement.InvokeAsync<JoinResult>("JoinDocument", Doc);
        await WaitForPeerAsync(host, replacement.ConnectionId!);
        var seed = PushSeedAsync(seeder);
        await store.Persisted.WaitAsync(Timeout);
        try
        {
            auth.Set("seeder", Doc, DocAccess.ReadOnly);
            await host.Access.RefreshAsync(Doc).WaitAsync(Timeout);
            store.ReleaseReturn();
            await seed.WaitAsync(Timeout);
            var hydrated = await join.WaitAsync(Timeout);
            Assert.NotNull(hydrated.Snapshot);
            Assert.Null(hydrated.SeedMarkdown);
            var live = host.Registry.TryGet(Doc)!;
            Assert.True(live.Seeded);
            Assert.False(live.Peers[seeder.ConnectionId!].CanWrite);
            Assert.Equal(replacement.ConnectionId, live.SnapshotterConnId);
            Assert.Equal(1, store.Inner.FlushCallsOf(Doc));
            await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(seeder));
        }
        finally { store.ReleaseReturn(); }
    }

    [Fact]
    public async Task A_refresh_looks_access_up_outside_the_gate_and_a_late_lookup_cannot_undo_a_newer_one()
    {
        var auth = new MutableAuthenticator();
        await using var host = await StartAsync(auth);
        await using var alice = host.Connection("alice");
        await alice.StartAsync(); await SeedAsync(alice);
        // A refresh looks alice up as a writer and stalls; the document keeps working meanwhile.
        var pause = auth.BlockNext("alice", Doc);
        var stale = host.Access.RefreshAsync(Doc);
        await pause.Entered.Task.WaitAsync(Timeout);
        Assert.Equal(1, await SendAsync(alice).WaitAsync(Timeout));

        // The revocation's refresh begins later and ends first: the stalled lookup cannot give write back.
        auth.Set("alice", Doc, DocAccess.ReadOnly);
        await host.Access.RefreshAsync(Doc).WaitAsync(Timeout);
        pause.Release.TrySetResult();
        await stale.WaitAsync(Timeout);
        Assert.False(host.Registry.TryGet(Doc)!.Peers[alice.ConnectionId!].CanWrite);
        await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(alice));
    }

    [Fact]
    public async Task Update_accepted_before_revocation_finishes_once_then_further_writes_are_denied()
    {
        var auth = new MutableAuthenticator();
        var store = new ControlledAppendStore { BlockReturn = true };
        await using var host = await StartAsync(auth, store);
        await using var alice = host.Connection("alice");
        await alice.StartAsync(); await SeedAsync(alice);
        var send = SendAsync(alice);
        await store.Appended.Task.WaitAsync(Timeout);
        auth.Set("alice", Doc, DocAccess.Denied);
        var refresh = host.Access.RefreshAsync(Doc);
        store.Release.TrySetResult();

        Assert.Equal(1, await send.WaitAsync(Timeout));
        await refresh.WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(alice));
        Assert.Equal(1, store.Inner.AppendCallsOf(Doc));
        Assert.Equal(1, host.Registry.TryGet(Doc)!.Seq);
    }

    [Fact]
    public async Task Queued_broadcast_rechecks_recipients_after_revocation()
    {
        var auth = new MutableAuthenticator();
        var store = new ControlledAppendStore();
        await using var host = await StartAsync(auth, store);
        await using var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await using var witness = host.Connection("witness");
        await alice.StartAsync(); await bob.StartAsync(); await witness.StartAsync();
        await SeedAsync(alice);
        await bob.InvokeAsync<JoinResult>("JoinDocument", Doc); await witness.InvokeAsync<JoinResult>("JoinDocument", Doc);
        var leaked = new ConcurrentQueue<long>();
        bob.On<string, byte[], long>("ReceiveUpdate", (_, _, seq) => leaked.Enqueue(seq));
        var received = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        witness.On<string, byte[], long>("ReceiveUpdate", (_, _, seq) => received.TrySetResult(seq));
        var precedingBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var live = host.Registry.TryGet(Doc)!;
        await live.Gate.WaitAsync();
        try { live.BroadcastTail = precedingBroadcast.Task; }
        finally { live.Gate.Release(); }
        try
        {
            var send = SendAsync(alice);
            await store.Appended.Task.WaitAsync(Timeout);
            auth.Set("bob", Doc, DocAccess.Denied);
            await host.Access.RefreshAsync(Doc).WaitAsync(Timeout);
            precedingBroadcast.TrySetResult();
            Assert.Equal(1, await send.WaitAsync(Timeout));
            Assert.Equal(1, await received.Task.WaitAsync(Timeout));
            await Assert.ThrowsAnyAsync<Exception>(() => bob.InvokeAsync<JoinResult>("JoinDocument", Doc));
            Assert.Empty(leaked);
        }
        finally { precedingBroadcast.TrySetResult(); }
    }

    [Fact]
    public async Task Noncooperative_authorization_timeout_denies_and_late_result_cannot_restore_access()
    {
        var auth = new MutableAuthenticator();
        await using var host = await HubTestHost.StartAsync(options =>
        {
            options.AccessRefreshInterval = TimeSpan.FromHours(1);
            options.AuthorizationTimeout = TimeSpan.FromMilliseconds(100);
        }, authenticator: auth);
        await using var alice = host.Connection("alice");
        await alice.StartAsync(); await SeedAsync(alice);
        var pause = auth.BlockNext("alice", Doc, ignoreCancellation: true);
        // Collaboration requests do not authorize; the refresh does, and its timeout denies.
        var refresh = host.Access.RefreshAsync(Doc);
        try
        {
            await pause.Entered.Task.WaitAsync(Timeout);
            await refresh.WaitAsync(Timeout);
            await Assert.ThrowsAsync<HubException>(() => SendAsync(alice).WaitAsync(Timeout));
            Assert.Equal(0, host.Registry.TryGet(Doc)!.Seq);
            Assert.DoesNotContain(alice.ConnectionId!, host.Registry.TryGet(Doc)!.Peers.Keys);
            auth.Set("alice", Doc, DocAccess.Denied);
            pause.Release.TrySetResult();
            await pause.Finished.Task.WaitAsync(Timeout);
            await Assert.ThrowsAsync<HubException>(() => alice.InvokeAsync<JoinResult>("JoinDocument", Doc));
            Assert.DoesNotContain(alice.ConnectionId!, host.Registry.TryGet(Doc)!.Peers.Keys);
        }
        finally { pause.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Failed_group_cleanup_cannot_restore_recipient_admission()
    {
        var auth = new MutableAuthenticator();
        var context = new FailingGroupCleanupContext();
        await using var host = await HubTestHost.StartAsync(options => options.AccessRefreshInterval = TimeSpan.FromHours(1),
            authenticator: auth, hubContext: context);
        await using var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await using var witness = host.Connection("witness");
        await alice.StartAsync(); await bob.StartAsync(); await witness.StartAsync(); await SeedAsync(alice);
        await bob.InvokeAsync<JoinResult>("JoinDocument", Doc); await witness.InvokeAsync<JoinResult>("JoinDocument", Doc);
        auth.Set("bob", Doc, DocAccess.Denied);
        await host.Access.RefreshAsync(Doc).WaitAsync(Timeout);
        await context.RemovalAttempted.Task.WaitAsync(Timeout);
        Assert.Equal(1, await SendAsync(alice));
        // Socket invocations are real; this transport seam records exact admitted recipients
        // and fails every group publication, so a fallback cannot hide the cleanup failure.
        var publications = context.Publications.Where(item => item.Method == "ReceiveUpdate").ToArray();
        Assert.NotEmpty(publications);
        Assert.All(publications, item => Assert.DoesNotContain(bob.ConnectionId!, item.ConnectionIds));
        Assert.Contains(publications, item => item.ConnectionIds.Contains(witness.ConnectionId!));
    }

    /// <summary>„Moje barva“ through the real hub: a peer holds its colour while it is in the document, and it is free again however
    /// the peer goes: leaving, losing access, disconnecting.</summary>
    [Fact]
    public async Task A_peers_colour_is_released_on_leave_revocation_and_disconnect()
    {
        var auth = new MutableAuthenticator();
        await using var host = await StartAsync(auth);
        await using var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await using var carol = host.Connection("carol");
        await alice.StartAsync(); await bob.StartAsync(); await carol.StartAsync(); await SeedAsync(alice);
        await bob.InvokeAsync<JoinResult>("JoinDocument", Doc); await carol.InvokeAsync<JoinResult>("JoinDocument", Doc);
        var doc = host.Registry.TryGet(Doc)!;
        var (aliceId, bobId, carolId) = (alice.ConnectionId!, bob.ConnectionId!, carol.ConnectionId!);
        Assert.All(new[] { aliceId, bobId, carolId }, id => Assert.NotNull(host.Registry.ColorOf(doc, id)));

        await bob.InvokeAsync("LeaveDocument", Doc).WaitAsync(Timeout);
        Assert.Null(host.Registry.ColorOf(doc, bobId));
        auth.Set("carol", Doc, DocAccess.Denied);
        await host.Access.RefreshAsync(Doc).WaitAsync(Timeout);
        Assert.Null(host.Registry.ColorOf(doc, carolId));
        await alice.StopAsync();
        using var deadline = new CancellationTokenSource(Timeout);
        while (host.Registry.ColorOf(doc, aliceId) is not null)
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class MutableAuthenticator : ICollabAuthenticator
    {
        private readonly ConcurrentDictionary<(string User, string Document), DocAccess> _access = new();
        private readonly ConcurrentDictionary<(string User, string Document), AuthorizationPause> _pauses = new();
        public void Set(string user, string document, DocAccess access) => _access[(user, document)] = access;
        public AuthorizationPause BlockNext(string user, string document, bool ignoreCancellation = false)
        {
            var pause = new AuthorizationPause { IgnoreCancellation = ignoreCancellation };
            Assert.True(_pauses.TryAdd((user, document), pause));
            return pause;
        }
        public Task<CollabUser?> AuthenticateAsync(HttpContext context, CancellationToken ct)
        {
            var user = context.Request.Query["user"].ToString();
            return Task.FromResult<CollabUser?>(new CollabUser(user, user));
        }
        private int _lookups;
        public int Lookups => Volatile.Read(ref _lookups);
        public async Task<DocAccess> AuthorizeAsync(CollabUser user, string documentId, CancellationToken ct)
        {
            Interlocked.Increment(ref _lookups);
            var access = _access.GetValueOrDefault((user.Id, documentId), DocAccess.Write);
            if (_pauses.TryRemove((user.Id, documentId), out var pause))
            {
                pause.Entered.TrySetResult();
                await pause.Release.Task.WaitAsync(pause.IgnoreCancellation ? CancellationToken.None : ct);
                pause.Finished.TrySetResult();
            }
            return access;
        }
    }

    private sealed class AuthorizationPause
    {
        public bool IgnoreCancellation { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FailingGroupCleanupContext : IHubContext<MarkdownSyncHub>
    {
        public sealed record Publication(string Method, IReadOnlyList<string> ConnectionIds);
        public ConcurrentQueue<Publication> Publications { get; } = new();
        public TaskCompletionSource RemovalAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IHubClients Clients { get; }
        public IGroupManager Groups { get; }
        public FailingGroupCleanupContext()
        {
            Clients = new RecordingClients(Publications);
            Groups = new FailingGroups(RemovalAttempted);
        }
        private sealed class FailingGroups(TaskCompletionSource attempted) : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
            {
                attempted.TrySetResult();
                return Task.FromException(new IOException("simulated group removal failure"));
            }
        }
        private sealed class RecordingClients(ConcurrentQueue<Publication> publications) : IHubClients
        {
            private sealed class RecipientProxy(ConcurrentQueue<Publication> publications, IReadOnlyList<string> recipients) : IClientProxy
            {
                public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
                {
                    publications.Enqueue(new(method, recipients.ToArray()));
                    return Task.CompletedTask;
                }
            }
            public IClientProxy Client(string connectionId) => new RecipientProxy(publications, [connectionId]);
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new RecipientProxy(publications, connectionIds);
            public IClientProxy All => throw new InvalidOperationException("Unfiltered publication is forbidden.");
            public IClientProxy AllExcept(IReadOnlyList<string> excluded) => throw new InvalidOperationException("Unfiltered publication is forbidden.");
            public IClientProxy Group(string groupName) => throw new InvalidOperationException("Group publication is forbidden.");
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => throw new InvalidOperationException("Group publication is forbidden.");
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new InvalidOperationException("Group publication is forbidden.");
            public IClientProxy User(string userId) => throw new InvalidOperationException("User publication is forbidden.");
            public IClientProxy Users(IReadOnlyList<string> userIds) => throw new InvalidOperationException("User publication is forbidden.");
        }
    }

    private sealed class ControlledAppendStore : IDocumentStore
    {
        public InMemoryDocumentStore Inner { get; } = new();
        public bool BlockReturn { get; init; }
        public TaskCompletionSource Appended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct) => Inner.LoadAsync(documentId, ct);
        public Task<MarkdownDocumentSchema?> BindSchemaAsync(string documentId, MarkdownDocumentSchema? schema, CancellationToken ct)
            => Inner.BindSchemaAsync(documentId, schema, ct);
        public Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct)
            => Inner.FlushAsync(documentId, snapshot, ct);
        public Task<bool> HasExternalChangeAsync(string documentId, CancellationToken ct) => Inner.HasExternalChangeAsync(documentId, ct);
        public async Task<AppendUpdateResult> AppendUpdateAsync(string documentId, Guid clientUpdateId, string userId,
            DateTimeOffset receivedAtUtc, ReadOnlyMemory<byte> update, CancellationToken ct)
        {
            var result = await Inner.AppendUpdateAsync(documentId, clientUpdateId, userId, receivedAtUtc, update, ct);
            Appended.TrySetResult();
            if (BlockReturn) await Release.Task.WaitAsync(ct);
            return result;
        }
    }
}
