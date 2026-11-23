using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Markdown.Sync;
using System.Security.Cryptography;
using System.Text.Json;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>
/// Drives <see cref="MarkdownSyncHub"/> over real HubConnections (WebSocket) against a
/// dynamic-port Kestrel host — the first SignalR test in the repo.
/// </summary>
public class MarkdownSyncHubTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static string NewUpdateId() => Guid.NewGuid().ToString("D");

    private static ProjectionSetDto Projections(long sequence, byte[] state, string markdown)
    {
        using var json = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[]}");
        var hash = Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant();
        return new ProjectionSetDto(sequence, hash, markdown, json.RootElement.Clone(), markdown);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        var deadline = TimeProvider.System.GetTimestamp() +
                       (long)(timeout.TotalSeconds * TimeProvider.System.TimestampFrequency);
        while (!condition())
        {
            var remainingTicks = deadline - TimeProvider.System.GetTimestamp();
            if (remainingTicks <= 0) break;

            var remaining = TimeSpan.FromSeconds((double)remainingTicks / TimeProvider.System.TimestampFrequency);
            if (!await timer.WaitForNextTickAsync().AsTask().WaitAsync(remaining)) break;
        }

        Assert.True(condition(), "condition not met within timeout");
    }

    [Fact]
    public async Task Peers_learn_when_a_snapshot_became_durable()
    {
        await using var host = await HubTestHost.StartAsync(configure: o => o.FlushInterval = TimeSpan.FromMilliseconds(20));
        await using var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await alice.StartAsync(); await bob.StartAsync();
        var stored = new List<long>();
        bob.On<string, long>("SnapshotStored", (_, seq) => { lock (stored) stored.Add(seq); });
        await alice.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        var initial = new byte[] { 0 };
        await alice.InvokeAsync("PushSnapshot", "doc1", 0L, initial, Projections(0, initial, "seed"));
        await bob.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        await alice.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        var next = new byte[] { 0, 1 };
        await alice.InvokeAsync("PushSnapshot", "doc1", 1L, next, Projections(1, next, "seed 1"));
        await WaitUntil(() => { lock (stored) return stored.Contains(1); }, Timeout);
        Assert.True(host.Store.FlushCallsOf("doc1") >= 2, "the announced sequence must already be flushed");
    }

    [Fact]
    public async Task A_connection_that_asks_for_gzip_gets_the_same_snapshot_compressed()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var alice = host.Connection("alice");
        await using var plain = host.Connection("bob");
        await using var compact = new HubConnectionBuilder().WithUrl($"{host.BaseUrl}/hubs/tiptap?user=carol&snapshot=gzip").Build();
        await alice.StartAsync(); await plain.StartAsync(); await compact.StartAsync();
        await alice.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        var state = new byte[20_000];
        for (var i = 0; i < state.Length; i++) state[i] = (byte)(i % 7);
        await alice.InvokeAsync("PushSnapshot", "doc1", 0L, state, Projections(0, state, "seed"));

        var raw = await plain.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        var gzip = await compact.InvokeAsync<JoinResult>("JoinDocument", "doc1");

        Assert.Null(raw.SnapshotEncoding);
        Assert.Equal(state, raw.Snapshot);
        Assert.Equal("gzip", gzip.SnapshotEncoding);
        Assert.True(gzip.Snapshot!.Length < state.Length / 10);
        using var inflated = new MemoryStream();
        using (var stream = new System.IO.Compression.GZipStream(new MemoryStream(gzip.Snapshot), System.IO.Compression.CompressionMode.Decompress))
            stream.CopyTo(inflated);
        Assert.Equal(state, inflated.ToArray());
        Assert.Equal(raw.Seq, gzip.Seq);
    }

    [Fact]
    public async Task A_connection_that_asks_gets_the_seed_compressed_and_may_push_the_state_compressed()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var alice = new HubConnectionBuilder().WithUrl($"{host.BaseUrl}/hubs/tiptap?user=alice&seed=gzip").Build();
        await alice.StartAsync();

        var joined = await alice.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        Assert.True(joined.IsSnapshotter);
        Assert.Null(joined.SeedMarkdown);
        Assert.False(joined.ProjectionsDerived); // an opaque relay keeps the client's projections
        using var seed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(new MemoryStream(joined.Seed!), System.IO.Compression.CompressionMode.Decompress))
            gzip.CopyTo(seed);
        using var json = JsonDocument.Parse(seed.ToArray());
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("markdown").ValueKind);

        var state = new byte[] { 0 };
        await alice.InvokeAsync("PushSnapshotGzip", "doc1", 0L, Gzip(state), Projections(0, state, ""));
        Assert.True(host.Registry.TryGet("doc1")!.Seeded);
    }

    [Fact]
    public async Task A_compressed_snapshot_is_inflated_only_within_the_document_size_limit()
    {
        await using var host = await HubTestHost.StartAsync(configure: o => o.MaxDocumentBytes = 64);
        await using var alice = host.Connection("alice");
        await alice.StartAsync();
        await alice.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        var bomb = new byte[10_000];
        var error = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
            alice.InvokeAsync("PushSnapshotGzip", "doc1", 0L, Gzip(bomb), Projections(0, bomb, "")));
        Assert.Contains("size limit", error.Message);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => alice.InvokeAsync("PushSnapshotGzip", "doc1", 0L, new byte[] { 1, 2, 3 }, Projections(0, bomb, "")));
        Assert.False(host.Registry.TryGet("doc1")!.Seeded);
    }

    [Fact]
    public async Task A_reader_compressed_snapshot_is_refused_before_it_is_inflated()
    {
        await using var host = await HubTestHost.StartAsync(configure: o => o.MaxDocumentBytes = 64);
        await using var writer = host.Connection("alice");
        await using var reader = host.Connection("ro-viewer");
        await writer.StartAsync();
        await reader.StartAsync();
        await SeedAsync(writer, "doc1");
        await reader.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        var bomb = new byte[10_000];

        // Inflating first would answer the size limit; the reader is told it may not write.
        var error = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
            reader.InvokeAsync("PushSnapshotGzip", "doc1", 0L, Gzip(bomb), Projections(0, bomb, "")));
        Assert.Contains("[READ_ONLY]", error.Message);
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest)) gzip.Write(bytes);
        return compressed.ToArray();
    }

    /// <summary>Join a doc as the seeding writer and immediately push an initial snapshot.</summary>
    private static async Task SeedAsync(HubConnection conn, string docId, string markdown = "")
    {
        var jr = await conn.InvokeAsync<JoinResult>("JoinDocument", docId);
        Assert.True(jr.IsSnapshotter);
        var state = new byte[] { 0 };
        await conn.InvokeAsync("PushSnapshot", docId, 0L, state, Projections(0, state, markdown));
    }

    [Fact]
    public async Task Without_an_authority_store_the_hub_accepts_no_protocol_2_message()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var alice = host.Connection("alice");
        await alice.StartAsync();
        await SeedAsync(alice, "doc1");
        var update = new byte[] { 1 };
        var sha = Convert.ToHexString(SHA256.HashData(update)).ToLowerInvariant();

        Assert.Equal(1, (await alice.InvokeAsync<AuthorityCapabilities>("GetCapabilities", "doc1")).ProtocolVersion);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => alice.InvokeAsync<AuthorityJoinResult>("JoinDocumentV2", "doc1",
            new MarkdownDocumentSchema(1, "rich", [])));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => alice.InvokeAsync<DocumentUpdateReceipt>("SendUpdateV2", "doc1",
            new AuthorityUpdateRequest("epoch", NewUpdateId(), sha, update)));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => alice.InvokeAsync<DocumentUpdateReceipt?>("GetUpdateReceiptV2", "doc1", "epoch", NewUpdateId()));
        Assert.Equal(0, host.Store.AppendCallsOf("doc1"));
        Assert.All(host.Registry.TryGet("doc1")!.Peers.Values, peer => Assert.Equal(1, peer.ProtocolVersion));
    }

    [Fact]
    public async Task First_writer_receives_lossless_rich_seed_and_other_peers_receive_only_the_snapshot()
    {
        await using var host = await HubTestHost.StartAsync();
        var rich = JsonSerializer.SerializeToElement(new { type = "doc", content = new[] {
            new { type = "paragraph", attrs = new { textAlign = "center" } } } });
        host.Store.Seed("rich-seed", "Lossy fallback");
        host.Store.SeedRich("rich-seed", rich);
        await using var a = host.Connection("alice");
        await a.StartAsync();
        var seed = await a.InvokeAsync<JoinResult>("JoinDocument", "rich-seed");
        Assert.True(seed.IsSnapshotter);
        Assert.True(JsonElement.DeepEquals(rich, seed.SeedEditorJson!.Value));
        byte[] state = [1, 2, 3];
        await a.InvokeAsync("PushSnapshot", "rich-seed", 0L, state, Projections(0, state, "Lossy fallback"));
        await using var b = host.Connection("bob");
        await b.StartAsync();
        var joined = await b.InvokeAsync<JoinResult>("JoinDocument", "rich-seed");
        Assert.Equal(state, joined.Snapshot);
        Assert.Null(joined.SeedEditorJson);
    }

    [Fact]
    public async Task Update_from_A_relays_to_B_in_same_group()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var a = host.Connection("alice");
        await using var b = host.Connection("bob");
        await a.StartAsync();
        await b.StartAsync();

        var received =
            new TaskCompletionSource<(byte[] Update, long Seq)>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.On<string, byte[], long>("ReceiveUpdate", (_, update, seq) => received.TrySetResult((update, seq)));

        await SeedAsync(a, "doc1");
        var jb = await b.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        Assert.False(jb.IsSnapshotter);

        var payload = new byte[] { 1, 2, 3, 4 };
        var seq = await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), payload);

        var got = await received.Task.WaitAsync(Timeout);
        Assert.Equal(payload, got.Update);
        Assert.Equal(seq, got.Seq);
    }

    [Fact]
    public async Task SendUpdate_does_not_ack_or_broadcast_when_durable_append_fails()
    {
        var store = new FaultInjectingDocumentStore { FailAppends = true };
        await using var host = await HubTestHost.StartAsync(store: store);
        await using var sender = host.Connection("alice");
        await using var receiver = host.Connection("bob");
        await sender.StartAsync();
        await receiver.StartAsync();

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.On<string, byte[], long>("ReceiveUpdate", (_, _, _) => received.TrySetResult());
        await SeedAsync(sender, "doc1");
        await receiver.InvokeAsync<JoinResult>("JoinDocument", "doc1");

        await Assert.ThrowsAnyAsync<Exception>(() => sender.InvokeAsync<long>(
            "SendUpdate", "doc1", Guid.NewGuid().ToString("D"), new byte[] { 1, 2, 3 }));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await received.Task.WaitAsync(TimeSpan.FromMilliseconds(300)));
        var live = host.Registry.TryGet("doc1");
        Assert.NotNull(live);
        Assert.Equal(0, live.Seq);
        Assert.Empty(live.Log);
    }

    /// <summary>Campaign 2026-11-05 F6: the generation fence is a final answer the client recognizes, not a transient storage failure.</summary>
    [Fact]
    public async Task SendUpdate_to_a_replaced_document_answers_superseded()
    {
        var store = new FaultInjectingDocumentStore();
        await using var host = await HubTestHost.StartAsync(store: store);
        await using var sender = host.Connection("alice");
        await sender.StartAsync();
        await SeedAsync(sender, "doc1");
        store.SupersedeAppends = true;

        var error = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => sender.InvokeAsync<long>(
            "SendUpdate", "doc1", Guid.NewGuid().ToString("D"), new byte[] { 1, 2, 3 }));

        Assert.True(error.Message.Contains("[SUPERSEDED]"), error.Message);
        Assert.Equal(0, host.Registry.TryGet("doc1")!.Seq);
    }

    /// <summary>The host stores what is unsaved once the last peer leaves (Atlas 3.6, the server's own checkpoint).</summary>
    [Fact]
    public async Task The_last_peer_leaving_tells_the_store()
    {
        var store = new LeaveRecordingStore();
        await using var host = await HubTestHost.StartAsync(store: store);
        await using var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await alice.StartAsync();
        await bob.StartAsync();
        await SeedAsync(alice, "doc1");
        await bob.InvokeAsync<JoinResult>("JoinDocument", "doc1");

        await alice.InvokeAsync("LeaveDocument", "doc1");
        Assert.Empty(store.Left);
        await bob.DisposeAsync();

        for (var waited = 0; store.Left.IsEmpty && waited < 5000; waited += 20) await Task.Delay(20);
        Assert.Equal("doc1", Assert.Single(store.Left));
    }

    private sealed class LeaveRecordingStore : IDocumentStore
    {
        private readonly InMemoryDocumentStore m_inner = new();
        public System.Collections.Concurrent.ConcurrentQueue<string> Left { get; } = new();
        public Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct) => m_inner.LoadAsync(documentId, ct);
        public Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct) => m_inner.FlushAsync(documentId, snapshot, ct);
        public Task<AppendUpdateResult> AppendUpdateAsync(string documentId, Guid clientUpdateId, string userId, DateTimeOffset receivedAtUtc,
            ReadOnlyMemory<byte> update, CancellationToken ct) => m_inner.AppendUpdateAsync(documentId, clientUpdateId, userId, receivedAtUtc, update, ct);
        public Task LastPeerLeftAsync(string documentId, CancellationToken ct) { Left.Enqueue(documentId); return Task.CompletedTask; }
    }

    [Fact]
    public async Task SendUpdate_same_client_id_returns_original_sequence_appends_once_and_replays_broadcast()
    {
        var store = new InMemoryDocumentStore();
        await using var host = await HubTestHost.StartAsync(store: store);
        await using var sender = host.Connection("alice");
        await using var receiver = host.Connection("bob");
        await sender.StartAsync();
        await receiver.StartAsync();
        await SeedAsync(sender, "doc1");
        await receiver.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        var firstBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var duplicateBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broadcasts = 0;
        receiver.On<string, byte[], long>("ReceiveUpdate", (_, _, _) =>
        {
            if (Interlocked.Increment(ref broadcasts) == 1) firstBroadcast.TrySetResult();
            else duplicateBroadcast.TrySetResult();
        });
        var updateId = Guid.NewGuid().ToString("D");

        var first = await sender.InvokeAsync<long>("SendUpdate", "doc1", updateId, new byte[] { 1, 2, 3 });
        await firstBroadcast.Task.WaitAsync(Timeout);
        var retry = await sender.InvokeAsync<long>("SendUpdate", "doc1", updateId, new byte[] { 1, 2, 3 });

        Assert.Equal(1, first);
        Assert.Equal(first, retry);
        Assert.Equal(1, store.AppendCallsOf("doc1"));
        Assert.Equal(1, store.LogCountOf("doc1"));
        await duplicateBroadcast.Task.WaitAsync(Timeout);
        Assert.Equal(2, Volatile.Read(ref broadcasts));
    }

    [Fact]
    public async Task Broadcast_failure_fails_the_invocation_and_same_update_id_retries_delivery_without_reappend()
    {
        var store = new InMemoryDocumentStore();
        var broadcasts = new FaultingBroadcastHubContext();
        await using var host = await HubTestHost.StartAsync(store: store, hubContext: broadcasts);
        await using var sender = host.Connection("alice");
        await sender.StartAsync();
        await SeedAsync(sender, "doc1");
        var updateId = NewUpdateId();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            sender.InvokeAsync<long>("SendUpdate", "doc1", updateId, new byte[] { 1, 2, 3 }));
        var retry = await sender.InvokeAsync<long>("SendUpdate", "doc1", updateId, new byte[] { 1, 2, 3 });

        Assert.Equal(1, retry);
        Assert.Equal(2, broadcasts.BroadcastCalls);
        Assert.Equal(1, store.AppendCallsOf("doc1"));
        Assert.Equal(1, store.LogCountOf("doc1"));
    }

    [Fact]
    public async Task Compacted_failed_broadcast_replays_original_payload_and_rejects_id_collision()
    {
        var store = new InMemoryDocumentStore();
        var broadcasts = new FaultingBroadcastHubContext();
        await using var host = await HubTestHost.StartAsync(
            configure: options => options.FlushInterval = TimeSpan.FromMilliseconds(20),
            store: store,
            hubContext: broadcasts);
        await using var sender = host.Connection("alice");
        await sender.StartAsync();
        await SeedAsync(sender, "doc1");
        var updateId = NewUpdateId();
        var original = new byte[] { 1, 2, 3 };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            sender.InvokeAsync<long>("SendUpdate", "doc1", updateId, original));
        var snapshotState = new byte[] { 9 };
        await sender.InvokeAsync("PushSnapshot", "doc1", 1L, snapshotState,
            Projections(1, snapshotState, "compacted"));
        Assert.Empty(host.Registry.TryGet("doc1")!.Log);
        await WaitUntil(() => store.LogCountOf("doc1") == 0, Timeout);

        var collision = await Assert.ThrowsAnyAsync<Exception>(() =>
            sender.InvokeAsync<long>("SendUpdate", "doc1", updateId, new byte[] { 7, 7, 7 }));
        Assert.Contains("[UPDATE_ID_COLLISION]", collision.Message);
        var replayedSequence = await sender.InvokeAsync<long>("SendUpdate", "doc1", updateId, original);

        Assert.Equal(1, replayedSequence);
        Assert.Equal(2, broadcasts.BroadcastCalls);
        Assert.Equal(original, broadcasts.BroadcastUpdates[0]);
        Assert.Equal(original, broadcasts.BroadcastUpdates[1]);
        Assert.Equal(1, store.AppendCallsOf("doc1"));
        Assert.Equal(0, store.LogCountOf("doc1"));
    }

    [Fact]
    public async Task Duplicate_without_replay_metadata_requests_a_new_update_id()
    {
        var store = new InMemoryDocumentStore();
        await using var host = await HubTestHost.StartAsync(store: store);
        await using var sender = host.Connection("alice");
        await sender.StartAsync();
        await SeedAsync(sender, "doc1");
        var updateId = NewUpdateId();
        var update = new byte[] { 1, 2, 3 };
        await sender.InvokeAsync<long>("SendUpdate", "doc1", updateId, update);
        var parsedUpdateId = Guid.Parse(updateId);
        var doc = host.Registry.TryGet("doc1")!;
        await doc.Gate.WaitAsync();
        try { doc.RecentUpdates.Remove(parsedUpdateId); }
        finally { doc.Gate.Release(); }

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            sender.InvokeAsync<long>("SendUpdate", "doc1", updateId, update));

        Assert.Contains("[RETRY_WITH_NEW_ID]", error.Message);
        Assert.Equal(1, store.AppendCallsOf("doc1"));
    }

    [Fact]
    public async Task Connection_without_authenticated_user_is_rejected()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var conn = new HubConnectionBuilder().WithUrl($"{host.BaseUrl}/hubs/tiptap").Build();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await conn.StartAsync();
            await conn.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        });
    }

    [Fact]
    public async Task Updates_do_not_leak_across_documents()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var a = host.Connection("alice");
        await using var b = host.Connection("bob");
        await a.StartAsync();
        await b.StartAsync();

        var leaked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.On<string, byte[], long>("ReceiveUpdate", (_, _, _) => leaked.TrySetResult());

        await SeedAsync(a, "doc1");
        await SeedAsync(b, "doc2");

        await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await leaked.Task.WaitAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task ReadOnly_user_cannot_send_updates_and_is_never_snapshotter()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var writer = host.Connection("alice");
        await writer.StartAsync();
        await SeedAsync(writer, "doc1");

        await using var ro = host.Connection("ro-viewer");
        await ro.StartAsync();
        var jr = await ro.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        Assert.False(jr.IsSnapshotter);
        Assert.False(jr.CanWrite);

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await ro.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 }));
        Assert.Contains("[READ_ONLY]", ex.Message);
    }

    [Fact]
    public async Task Denied_document_rejects_join()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var a = host.Connection("alice");
        await a.StartAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await a.InvokeAsync<JoinResult>("JoinDocument", "denied:secret"));
        Assert.Contains("denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrent_joiner_waits_for_the_seed_snapshot()
    {
        await using var host = await HubTestHost.StartAsync();
        host.Store.Seed("doc1", "# Seeded\n\nhello");
        await using var a = host.Connection("alice"); // seeding snapshotter
        await using var b = host.Connection("bob"); // concurrent joiner
        await a.StartAsync();
        await b.StartAsync();

        var ja = await a.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        Assert.True(ja.IsSnapshotter);
        Assert.Null(ja.Snapshot);
        Assert.Equal("# Seeded\n\nhello", ja.SeedMarkdown);

        // B joins while seeding is in progress → must block until A pushes.
        var bJoin = b.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        await WaitUntil(() => host.Registry.TryGet("doc1")?.SeedWaiters.Count == 1, Timeout);
        Assert.False(bJoin.IsCompleted);

        var seededState = new byte[] { 10, 20, 30 };
        await a.InvokeAsync("PushSnapshot", "doc1", 0L, seededState, Projections(0, seededState, "# Seeded\n\nhello"));

        var jb = await bJoin.WaitAsync(Timeout);
        Assert.Equal(1, host.Store.FlushCallsOf("doc1"));
        Assert.False(jb.IsSnapshotter);
        Assert.Equal(new byte[] { 10, 20, 30 }, jb.Snapshot);
        Assert.Null(jb.SeedMarkdown);
    }

    [Fact]
    public async Task Join_rejects_an_invalid_document_id_before_registry_or_authorization()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var connection = host.Connection("alice");
        await connection.StartAsync();

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            connection.InvokeAsync<JoinResult>("JoinDocument", "../outside"));

        Assert.Contains("document id", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, host.Registry.Count);
    }

    [Fact]
    public async Task Snapshot_older_than_the_durable_baseline_is_dropped()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var connection = host.Connection("alice");
        await connection.StartAsync();
        await SeedAsync(connection, "doc1");
        await connection.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        var current = new byte[] { 2 };
        await connection.InvokeAsync("PushSnapshot", "doc1", 1L, current, Projections(1, current, "current"));

        // A newer baseline (another push, or the relay's own compaction) supersedes it: not adopted, and no error the client retries.
        var stale = new byte[] { 3 };
        await connection.InvokeAsync("PushSnapshot", "doc1", 0L, stale, Projections(0, stale, "stale"));

        Assert.Equal(1, host.Registry.TryGet("doc1")?.SnapshotSeq);
        Assert.Equal(current, host.Registry.TryGet("doc1")?.Snapshot);
    }

    [Fact]
    public async Task An_update_whose_tab_closes_while_it_commits_stays_known_to_the_live_document()
    {
        var store = new CommitThenCheckStore();
        await using var host = await HubTestHost.StartAsync(store: store);
        var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await alice.StartAsync();
        await bob.StartAsync();
        await SeedAsync(alice, "doc1");
        await bob.InvokeAsync<JoinResult>("JoinDocument", "doc1");

        store.HoldAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = alice.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        await store.AppendStarted.WaitAsync(Timeout);
        await alice.DisposeAsync(); // the tab closes while its update commits
        await WaitUntil(() => store.AppendToken.IsCancellationRequested || !store.AppendToken.CanBeCanceled, Timeout);
        store.HoldAppend.SetResult();
        await Record.ExceptionAsync(() => pending);

        // Before the fix: "Expected update sequence 1, received 2" for this and every later update.
        Assert.Equal(2, await bob.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 2 }));
        Assert.Equal(2, host.Registry.TryGet("doc1")!.Seq);
    }

    [Fact]
    public async Task A_live_document_behind_its_durable_log_reloads_it_and_relays_what_it_missed()
    {
        var store = new CommitThenCheckStore { FailAfterNextAppend = true };
        await using var host = await HubTestHost.StartAsync(store: store);
        await using var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await alice.StartAsync();
        await bob.StartAsync();
        var relayed = new System.Collections.Concurrent.ConcurrentQueue<long>();
        bob.On<string, byte[], long>("ReceiveUpdate", (_, _, seq) => relayed.Enqueue(seq));
        await SeedAsync(alice, "doc1");
        await bob.InvokeAsync<JoinResult>("JoinDocument", "doc1");

        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() =>
            alice.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 }));
        Assert.Equal(0, host.Registry.TryGet("doc1")!.Seq); // durable at 1, unknown to the live document

        Assert.Equal(2, await alice.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 2 }));
        await WaitUntil(() => relayed.Count == 2, Timeout);
        Assert.Equal([1L, 2L], relayed.ToArray());
        Assert.Equal(2, (await bob.InvokeAsync<JoinResult>("JoinDocument", "doc1")).Updates.Length);
    }

    [Fact]
    public async Task Seeding_snapshotter_disconnect_lets_a_waiting_writer_reseed_instead_of_wedging()
    {
        await using var host = await HubTestHost.StartAsync();
        host.Store.Seed("doc1", "seed md");
        await using var a = host.Connection("alice"); // seeding snapshotter
        var b = host.Connection("bob"); // concurrent joiner (kept open past 'a')
        await a.StartAsync();
        await b.StartAsync();

        var ja = await a.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        Assert.True(ja.IsSnapshotter);

        var bJoin = b.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        await WaitUntil(() => host.Registry.TryGet("doc1")?.SeedWaiters.Count == 1, Timeout);
        Assert.False(bJoin.IsCompleted); // blocked waiting for the seed

        // A disconnects before ever pushing the seed snapshot.
        await a.StopAsync();

        // B must recover by becoming the new seeding snapshotter (not hang to timeout).
        var jb = await bJoin.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(jb.IsSnapshotter);
        Assert.Equal("seed md", jb.SeedMarkdown);
        Assert.Equal(1, host.Registry.ActiveDocumentCount); // no ghost peers left behind

        await b.DisposeAsync();
    }

    [Fact]
    public async Task ReadOnly_first_joiner_cannot_seed_or_mutate_an_unseeded_document()
    {
        await using var host = await HubTestHost.StartAsync();
        host.Store.Seed("rodoc", "read me");
        await using var ro = host.Connection("ro-viewer");
        await ro.StartAsync();

        var joinError =
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await ro.InvokeAsync<JoinResult>("JoinDocument", "rodoc"));
        Assert.Contains("writer", joinError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[READ_ONLY]", joinError.Message);

        var snapshotError = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await ro.InvokeAsync("PushSnapshot", "rodoc", 0L, new byte[] { 1 },
                Projections(0, new byte[] { 1 }, "forged")));
        Assert.Contains("not joined", snapshotError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(host.Store.SnapshotOf("rodoc"));
    }

    [Fact]
    public async Task Update_before_the_initial_snapshot_is_durable_is_rejected()
    {
        var store = new InMemoryDocumentStore();
        await using var host = await HubTestHost.StartAsync(store: store);
        await using var writer = host.Connection("alice");
        await writer.StartAsync();

        var join = await writer.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        Assert.True(join.IsSnapshotter);
        Assert.True(join.CanWrite);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => writer.InvokeAsync<long>(
            "SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 }));

        Assert.Contains("initial snapshot", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.AppendCallsOf("doc1"));
        Assert.Equal(0, store.LogCountOf("doc1"));
    }

    [Fact]
    public async Task Initial_snapshot_storage_does_not_hold_the_document_lifecycle_gate()
    {
        var store = new BlockingInitialFlushStore();
        await using var host = await HubTestHost.StartAsync(store: store);
        await using var writer = host.Connection("alice");
        await writer.StartAsync();
        var joined = await writer.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        var state = new byte[] { 1 };
        var pushing = writer.InvokeAsync("PushSnapshot", "doc1", 0L, state, Projections(0, state, "seed"));
        await store.FlushEntered.WaitAsync(Timeout);

        var doc = host.Registry.TryGet("doc1")!;
        var acquired = await doc.Gate.WaitAsync(TimeSpan.FromMilliseconds(300));
        if (acquired) doc.Gate.Release();
        store.ReleaseFlush();
        await pushing;

        Assert.True(joined.IsSnapshotter);
        Assert.True(acquired, "initial storage I/O held the document lifecycle gate");
        Assert.True(doc.Seeded);
    }

    [Fact]
    public async Task Seeder_disconnect_cancels_blocked_storage_and_releases_the_next_writer()
    {
        var store = new BlockingInitialFlushStore();
        await using var host = await HubTestHost.StartAsync(configure: options =>
            options.SeedWait = TimeSpan.FromSeconds(3), store: store);
        await using var first = host.Connection("alice");
        await using var next = host.Connection("bob");
        await first.StartAsync();
        await next.StartAsync();
        await first.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        var state = new byte[] { 1 };
        var blockedPush = first.InvokeAsync("PushSnapshot", "doc1", 0L, state, Projections(0, state, "seed"));
        await store.FlushEntered.WaitAsync(Timeout);
        var waitingJoin = next.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        await WaitUntil(() => host.Registry.TryGet("doc1")?.SeedWaiters.Count == 1, Timeout);

        await first.StopAsync();
        await Assert.ThrowsAnyAsync<Exception>(async () => await blockedPush);
        var promoted = await waitingJoin.WaitAsync(Timeout);

        Assert.True(promoted.IsSnapshotter);
        Assert.Null(promoted.Snapshot);
        Assert.NotNull(promoted.SeedMarkdown);
    }

    [Fact]
    public async Task Seeder_disconnect_after_durable_write_hydrates_waiters_from_that_snapshot()
    {
        var store = new PersistedThenBlockingInitialFlushStore();
        await using var host = await HubTestHost.StartAsync(configure: options =>
            options.SeedWait = TimeSpan.FromSeconds(3), store: store);
        await using var first = host.Connection("alice");
        await using var next = host.Connection("bob");
        await first.StartAsync();
        await next.StartAsync();
        await first.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        var state = new byte[] { 42 };
        var blockedPush = first.InvokeAsync("PushSnapshot", "doc1", 0L, state,
            Projections(0, state, "durable seed"));
        await store.Persisted.WaitAsync(Timeout);

        await first.StopAsync();
        var waitingJoin = next.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        await WaitUntil(() => host.Registry.TryGet("doc1")?.SeedWaiters.Count == 1, Timeout);
        store.ReleaseReturn();
        var joined = await waitingJoin.WaitAsync(Timeout);

        await Assert.ThrowsAnyAsync<Exception>(async () => await blockedPush);
        Assert.Equal(state, joined.Snapshot);
        Assert.Null(joined.SeedMarkdown);
        Assert.True(joined.IsSnapshotter);
        Assert.Equal(state, store.Inner.SnapshotOf("doc1"));
        Assert.True(host.Registry.TryGet("doc1")!.Seeded);
    }

    [Fact]
    public async Task Snapshotter_is_reelected_when_the_current_one_leaves()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var a = host.Connection("alice"); // first writer → snapshotter
        await using var b = host.Connection("bob"); // second writer
        await a.StartAsync();
        await b.StartAsync();

        var reelected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.On<string, bool>("SnapshotterChanged", (docId, youAre) =>
        {
            if (youAre) reelected.TrySetResult(docId);
        });

        await SeedAsync(a, "doc1");
        var jb = await b.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        Assert.False(jb.IsSnapshotter);

        await a.StopAsync(); // snapshotter leaves → B elected + told to snapshot

        Assert.Equal("doc1", await reelected.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Snapshotter_role_moves_from_a_passive_peer_to_the_first_one_that_writes()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var a = host.Connection("alice"); // seeds, then only reads
        await using var b = host.Connection("bob"); // types
        await a.StartAsync();
        await b.StartAsync();
        var lost = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.On<string, bool>("SnapshotterChanged", (_, youAre) => { if (!youAre) lost.TrySetResult(true); });
        b.On<string, bool>("SnapshotterChanged", (_, youAre) => { if (youAre) gained.TrySetResult(true); });

        await SeedAsync(a, "doc1");
        Assert.False((await b.InvokeAsync<JoinResult>("JoinDocument", "doc1")).IsSnapshotter);
        await b.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });

        Assert.True(await gained.Task.WaitAsync(Timeout));
        Assert.True(await lost.Task.WaitAsync(Timeout));
        var doc = host.Registry.TryGet("doc1")!;
        Assert.Equal(doc.Peers.Values.Single(peer => peer.User.Id == "bob").ConnectionId, doc.SnapshotterConnId);

        // Once the snapshotter writes too, a later writer leaves it where it is.
        await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 2 });
        Assert.Equal(doc.Peers.Values.Single(peer => peer.User.Id == "bob").ConnectionId, doc.SnapshotterConnId);
    }

    [Fact]
    public async Task Snapshotter_role_moves_from_a_writer_that_went_quiet_to_the_next_one_that_writes()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var a = host.Connection("alice"); // seeds and types, then goes offline without leaving
        await using var b = host.Connection("bob");
        await a.StartAsync();
        await b.StartAsync();
        var gained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.On<string, bool>("SnapshotterChanged", (_, youAre) => { if (youAre) gained.TrySetResult(true); });

        await SeedAsync(a, "doc1");
        await b.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        var doc = host.Registry.TryGet("doc1")!;
        var alice = doc.Peers.Values.Single(peer => peer.User.Id == "alice").ConnectionId;
        // Bob writing while Alice's snapshots keep up leaves the role with her.
        await b.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 2 });
        Assert.Equal(alice, doc.SnapshotterConnId);

        // Alice's update has waited for a snapshot longer than her editor ever lets one wait: Bob's next update takes the role.
        await doc.Gate.WaitAsync();
        try { doc.Log[0] = doc.Log[0] with { ReceivedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30) }; }
        finally { doc.Gate.Release(); }
        await b.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 3 });

        Assert.True(await gained.Task.WaitAsync(Timeout));
        Assert.Equal(doc.Peers.Values.Single(peer => peer.User.Id == "bob").ConnectionId, doc.SnapshotterConnId);
    }

    [Fact]
    public async Task Flush_persists_snapshot_and_log_and_truncates_on_snapshot()
    {
        var store = new InMemoryDocumentStore();
        await using var host = await HubTestHost.StartAsync(
            configure: o => o.FlushInterval = TimeSpan.FromMilliseconds(150), store: store);
        await using var a = host.Connection("alice");
        await a.StartAsync();

        await SeedAsync(a, "doc1");
        await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 }); // seq 1
        await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 2 }); // seq 2
        await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 3 }); // seq 3

        await WaitUntil(() => store.LogCountOf("doc1") >= 3, TimeSpan.FromSeconds(3));

        // Snapshot at baseline 2 → the store truncates its log to records with seq > 2 (keeps seq 3).
        await a.InvokeAsync("PushSnapshot", "doc1", 2L, new byte[] { 99 },
            Projections(2, new byte[] { 99 }, "final md"));
        await WaitUntil(() => store.FlushCallsOf("doc1") >= 2, TimeSpan.FromSeconds(3));

        Assert.Equal(new byte[] { 99 }, store.SnapshotOf("doc1"));
        Assert.Equal("final md", store.MarkdownOf("doc1"));
        Assert.Equal(1, store.LogCountOf("doc1"));
    }

    [Fact]
    public async Task Flush_failure_broadcasts_the_degraded_banner()
    {
        var store = new FaultInjectingDocumentStore { SuccessfulFlushesBeforeFailure = 1 };
        await using var host = await HubTestHost.StartAsync(
            configure: o => o.FlushInterval = TimeSpan.FromMilliseconds(150),
            store: store);
        await using var a = host.Connection("alice");
        await a.StartAsync();

        var degraded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.On<string, bool>("FlushDegraded", (_, isDegraded) =>
        {
            if (isDegraded) degraded.TrySetResult(true);
        });

        await SeedAsync(a, "doc1");
        await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        await a.InvokeAsync("PushSnapshot", "doc1", 1L, new byte[] { 2 },
            Projections(1, new byte[] { 2 }, "x")); // marks dirty; flush tick will fail

        Assert.True(await degraded.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Warm_document_is_not_evicted_while_persistence_is_failing()
    {
        var store = new FaultInjectingDocumentStore { SuccessfulFlushesBeforeFailure = 1 };
        await using var host = await HubTestHost.StartAsync(
            configure: o =>
            {
                o.FlushInterval = TimeSpan.FromMilliseconds(50);
                o.WarmKeep = TimeSpan.FromMilliseconds(1);
            },
            store: store);
        await using var writer = host.Connection("alice");
        await writer.StartAsync();

        await SeedAsync(writer, "doc1");
        await writer.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        await writer.InvokeAsync("PushSnapshot", "doc1", 1L, new byte[] { 2 },
            Projections(1, new byte[] { 2 }, "must survive"));
        await writer.StopAsync();

        var attemptsWhenWarm = store.WriteAttempts;
        await WaitUntil(() => store.WriteAttempts >= attemptsWhenWarm + 4, Timeout);

        Assert.Equal(1, host.Registry.Count);
    }

    [Fact]
    public async Task Snapshot_of_a_document_the_store_replaced_is_dropped_once_and_the_warm_document_evicts()
    {
        // A save or discard moved the page to a new generation while the relay still held a dirty snapshot of the old one:
        // the flush retried "Společná editace má novější stav" every tick and the warm document never evicted.
        var store = new FaultInjectingDocumentStore { FlushesBeforeSuperseded = 1 };
        await using var host = await HubTestHost.StartAsync(
            configure: o =>
            {
                o.FlushInterval = TimeSpan.FromMilliseconds(50);
                o.WarmKeep = TimeSpan.FromMilliseconds(1);
            },
            store: store);
        await using var writer = host.Connection("alice");
        await writer.StartAsync();

        await SeedAsync(writer, "doc1");
        await writer.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        await writer.InvokeAsync("PushSnapshot", "doc1", 1L, new byte[] { 2 },
            Projections(1, new byte[] { 2 }, "replaced"));
        await writer.StopAsync();

        await WaitUntil(() => host.Registry.Count == 0, Timeout);
        // The seed, the update and the one superseded snapshot; nothing retried.
        Assert.Equal(3, store.WriteAttempts);
    }

    [Fact]
    public async Task Update_exceeding_the_document_cap_is_rejected_with_a_size_limit_error()
    {
        // A-M5 — the 4 MiB doc cap (tiny here) rejects oversized updates; the message must contain
        // "size limit exceeded" so the client's onDocumentFull detector surfaces the CZ banner.
        await using var host = await HubTestHost.StartAsync(configure: o => o.MaxDocumentBytes = 16);
        await using var a = host.Connection("alice");
        await a.StartAsync();
        await SeedAsync(a, "doc1");

        var oversized = new byte[64];
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), oversized));
        Assert.Contains("size limit exceeded", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_update_beyond_the_update_limit_is_rejected_with_a_size_limit_error()
    {
        await using var host = await HubTestHost.StartAsync(services: s => s.AddSingleton(new DocumentAuthorityOptions { MaxUpdateBytes = 8 }));
        await using var a = host.Connection("alice");
        await a.StartAsync();
        await SeedAsync(a, "doc1");

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[16]));
        Assert.Contains("[UPDATE_TOO_LARGE]", ex.Message);
        Assert.Contains("size limit exceeded", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Updates_beyond_the_per_second_limit_are_refused_for_a_retry()
    {
        await using var host = await HubTestHost.StartAsync(services: s => s
            .AddSingleton(new DocumentAuthorityOptions { MaxUpdatesPerSecond = 2 }).AddSingleton<TimeProvider>(new FrozenClock()));
        await using var a = host.Connection("alice");
        await a.StartAsync();
        await SeedAsync(a, "doc1");

        await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 }));
        Assert.Contains("[RATE_LIMITED]", ex.Message);
    }

    /// <summary>One second that never ends: the rate window does not roll over mid-test.</summary>
    private sealed class FrozenClock : TimeProvider
    {
        private readonly DateTimeOffset m_now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => m_now;
    }

    [Fact]
    public async Task Graceful_shutdown_final_flushes_a_dirty_document()
    {
        // A-M5 — a planned restart must lose nothing. With a 60 s tick (so no periodic flush fires
        // in the test window) the dirty snapshot is persisted only by the shutdown path.
        var store = new InMemoryDocumentStore();
        var host = await HubTestHost.StartAsync(
            configure: o => o.FlushInterval = TimeSpan.FromSeconds(60), store: store);
        await using var a = host.Connection("alice");
        await a.StartAsync();

        await SeedAsync(a, "doc1"); // initial seed is synchronously durable
        await a.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        await a.InvokeAsync("PushSnapshot", "doc1", 1L, new byte[] { 42 },
            Projections(1, new byte[] { 42 }, "shutdown md")); // newer snapshot is dirty
        Assert.Equal(1, store.FlushCallsOf("doc1")); // only the initial seed has reached storage

        await host.DisposeAsync(); // app StopAsync → DocumentFlushService.StopAsync → final flush

        Assert.Equal(new byte[] { 42 }, store.SnapshotOf("doc1"));
        Assert.Equal("shutdown md", store.MarkdownOf("doc1"));
    }

    [Fact]
    public async Task Graceful_shutdown_retries_a_transient_final_flush_failure_until_durable()
    {
        var store = new TransientFlushFailureStore();
        var host = await HubTestHost.StartAsync(configure: options =>
        {
            options.FlushInterval = TimeSpan.FromSeconds(60);
            options.ShutdownSnapshotWait = TimeSpan.FromSeconds(1);
        }, store: store);
        await using var writer = host.Connection("alice");
        await writer.StartAsync();
        await SeedAsync(writer, "doc1");
        await writer.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        var finalState = new byte[] { 42 };
        await writer.InvokeAsync("PushSnapshot", "doc1", 1L, finalState,
            Projections(1, finalState, "retried shutdown snapshot"));
        store.FailNextFlush();

        await host.DisposeAsync();

        Assert.True(store.FlushAttempts >= 3); // initial seed + failed final + successful retry
        Assert.Equal(finalState, store.Inner.SnapshotOf("doc1"));
        Assert.Equal("retried shutdown snapshot", store.Inner.MarkdownOf("doc1"));
    }

    [Fact]
    public async Task Graceful_shutdown_bounds_a_stalled_snapshot_request_and_still_final_flushes()
    {
        var store = new InMemoryDocumentStore();
        var stalledHub = new BlockingSnapshotRequestHubContext();
        var host = await HubTestHost.StartAsync(configure: options =>
        {
            options.FlushInterval = TimeSpan.FromSeconds(60);
            options.ShutdownSnapshotWait = TimeSpan.FromMilliseconds(100);
        }, store: store, hubContext: stalledHub);
        await using var writer = host.Connection("alice");
        await writer.StartAsync();
        await SeedAsync(writer, "doc1");
        await writer.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        var finalState = new byte[] { 42 };
        await writer.InvokeAsync("PushSnapshot", "doc1", 1L, finalState,
            Projections(1, finalState, "bounded shutdown snapshot"));

        var stopping = host.DisposeAsync().AsTask();
        await stalledHub.RequestStarted.WaitAsync(Timeout);
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(finalState, store.SnapshotOf("doc1"));
        Assert.Equal("bounded shutdown snapshot", store.MarkdownOf("doc1"));
    }

    [Fact]
    public async Task Graceful_shutdown_waits_for_the_requested_snapshot_revision_before_final_flush()
    {
        var store = new InMemoryDocumentStore();
        var host = await HubTestHost.StartAsync(configure: options =>
        {
            options.FlushInterval = TimeSpan.FromSeconds(60);
            options.ShutdownSnapshotWait = TimeSpan.FromSeconds(3);
        }, store: store);
        await using var writer = host.Connection("alice");
        await writer.StartAsync();
        await SeedAsync(writer, "doc1");
        await writer.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), new byte[] { 1 });
        writer.On<string>("RequestSnapshot", async documentId =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1700));
            var finalState = new byte[] { 42 };
            await writer.InvokeAsync("PushSnapshot", documentId, 1L, finalState,
                Projections(1, finalState, "late shutdown snapshot"));
        });

        await host.DisposeAsync();

        Assert.Equal(new byte[] { 42 }, store.SnapshotOf("doc1"));
        Assert.Equal("late shutdown snapshot", store.MarkdownOf("doc1"));
    }

    [Fact]
    public async Task Awareness_is_relayed_with_the_handshake_identity_but_never_returned_by_join()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var a = host.Connection("alice");
        await using var b = host.Connection("bob");
        await a.StartAsync();
        await b.StartAsync();

        var awareness = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        b.On<string, byte[]>("ReceiveAwareness", (_, payload) => awareness.Enqueue(payload));

        await SeedAsync(a, "doc1");
        await b.InvokeAsync<JoinResult>("JoinDocument", "doc1");

        // Two states in one message (another client's included) are dropped; one owned state arrives under alice's name.
        await a.InvokeAsync("SendAwareness", "doc1", AuthorityAwarenessTests.Frame("{}", count: 2));
        await a.InvokeAsync("SendAwareness", "doc1", AuthorityAwarenessTests.Frame("{\"user\":{\"id\":\"bob\",\"name\":\"Bob\"},\"cursor\":null}"));

        var deadline = DateTime.UtcNow + Timeout;
        while (awareness.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20);
        var received = Assert.Single(awareness);
        var json = System.Text.Encoding.UTF8.GetString(received);
        Assert.Contains("\"user\":{\"id\":\"alice\",\"name\":\"alice\"", json);
        Assert.DoesNotContain("Bob", json);
    }

    [Fact]
    public async Task A_connection_that_goes_without_its_own_removal_is_removed_from_the_others_presence()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var a = host.Connection("alice");
        var b = host.Connection("bob");
        await a.StartAsync();
        await b.StartAsync();
        var awareness = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        a.On<string, byte[]>("ReceiveAwareness", (_, payload) => awareness.Enqueue(payload));
        await SeedAsync(a, "doc1");
        await b.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        await b.InvokeAsync("SendAwareness", "doc1", AuthorityAwarenessTests.Frame("{}", client: 7, clock: 3));
        await WaitUntil(() => awareness.Count == 1, Timeout);

        // The tab closes, or its own removal is cancelled with the connection it was sent on: the server removes it for the others.
        await b.DisposeAsync();

        await WaitUntil(() => awareness.Count == 2, Timeout);
        Assert.Equal(AuthorityAwarenessTests.Frame("null", client: 7, clock: 4), awareness.Last());
    }

    /// <summary>
    /// An agent's presence (LiveDocumentWriter.ShowAsync) is the server's: a peer that read its client id from the awareness can neither
    /// take it for its own awareness (hiding or replacing the AI for everyone) nor write Yjs structs as it (the editors would type its text
    /// in with the AI caret, agentReveal.ts). The peer's own client id still works.
    /// </summary>
    [Fact]
    public async Task A_peer_can_neither_show_nor_write_as_a_collaborator_the_server_shows()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var a = host.Connection("alice");
        await using var b = host.Connection("bob");
        await a.StartAsync();
        await b.StartAsync();
        var awareness = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        var updates = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        a.On<string, byte[]>("ReceiveAwareness", (_, payload) => awareness.Enqueue(payload));
        a.On<string, byte[], long>("ReceiveUpdate", (_, update, _) => updates.Enqueue(update));
        await SeedAsync(a, "doc1");
        await b.InvokeAsync<JoinResult>("JoinDocument", "doc1");
        await host.Writer.ShowAsync("doc1", 4242, 1, new CollabUser("ai:alice", "Alice · AI (MCP)"));
        await WaitUntil(() => awareness.Count == 1, Timeout);

        await b.InvokeAsync("SendAwareness", "doc1", AuthorityAwarenessTests.Frame("null", client: 4242, clock: 9));
        var forged = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => b.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), TextAt(4242)));
        Assert.Contains("[INVALID_CONTENT]", forged.Message);
        // Its own client id is as before: its awareness and its text go out.
        await b.InvokeAsync("SendAwareness", "doc1", AuthorityAwarenessTests.Frame("{}", client: 7, clock: 1));
        await b.InvokeAsync<long>("SendUpdate", "doc1", NewUpdateId(), TextAt(7));
        await WaitUntil(() => awareness.Count == 2 && updates.Count == 1, Timeout);
        Assert.Equal(7, awareness.Last()[1]);
        Assert.Equal(TextAt(7), Assert.Single(updates));
    }

    /// <summary>A Yjs update of one struct of <paramref name="client"/> (clock 0): "x" in the root type "t".</summary>
    private static byte[] TextAt(int client)
    {
        var id = new List<byte>();
        for (; client > 127; client >>= 7) id.Add((byte)(client & 127 | 128));
        id.Add((byte)client);
        return [1, 1, .. id, 0, 4, 1, 1, (byte)'t', 1, (byte)'x', 0];
    }
}
