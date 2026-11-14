using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>The opted-in managed content guard in front of the relay, driven over real HubConnections.</summary>
public class ContentGuardHubTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static string NewUpdateId() => Guid.NewGuid().ToString("D");

    private static ProjectionSetDto Projections(long sequence, byte[] state)
    {
        using var json = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[]}");
        return new ProjectionSetDto(sequence, Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant(), "", json.RootElement.Clone(), "");
    }

    private static Task<HubTestHost> StartGuardedAsync(IReservedNodeGuard? reservedNodes = null)
        => HubTestHost.StartAsync(services: services =>
        {
            services.AddAtlasMarkdownContentGuard();
            if (reservedNodes is not null) services.AddSingleton(reservedNodes);
        });

    private static async Task SeedAsync(HubConnection connection, string documentId, byte[] state)
    {
        Assert.True((await connection.InvokeAsync<JoinResult>("JoinDocument", documentId)).IsSnapshotter);
        await connection.InvokeAsync("PushSnapshot", documentId, 0L, state, Projections(0, state));
    }

    [Fact]
    public async Task A_malicious_writer_is_refused_while_honest_peers_keep_editing()
    {
        await using var host = await StartGuardedAsync();
        await using var alice = host.Connection("alice");
        await using var bob = host.Connection("bob");
        await using var mallory = host.Connection("mallory");
        await alice.StartAsync(); await bob.StartAsync(); await mallory.StartAsync();
        var received = new List<byte[]>();
        bob.On<string, byte[], long>("ReceiveUpdate", (_, update, _) => { lock (received) received.Add(update); });

        await SeedAsync(alice, "doc", YjsCorpus.Read("hostile/000-baseline.state.bin"));
        await bob.InvokeAsync<JoinResult>("JoinDocument", "doc");
        await mallory.InvokeAsync<JoinResult>("JoinDocument", "doc");

        var legit = YjsCorpus.Read("hostile/014-legit_type_control_.update.bin");
        Assert.Equal(1, await alice.InvokeAsync<long>("SendUpdate", "doc", NewUpdateId(), legit));
        foreach (var hostile in new[]
                 {
                     "hostile/003-raw_secretReference_block.update.bin",
                     "hostile/012-secret_inserted_and_deleted_in_the_same_update_smuggle_.update.bin",
                     "hostile/001-unknown_top-level_node.update.bin",
                     "hostile/010-other_root_map.update.bin",
                 })
        {
            var error = await Assert.ThrowsAsync<HubException>(() =>
                mallory.InvokeAsync<long>("SendUpdate", "doc", NewUpdateId(), YjsCorpus.Read(hostile)));
            Assert.Contains("[INVALID_CONTENT]", error.Message);
        }
        var deleteOnly = YjsCorpus.Read("hostile/015-legit_delete-only_control_.update.bin");
        Assert.Equal(2, await alice.InvokeAsync<long>("SendUpdate", "doc", NewUpdateId(), deleteOnly));

        Assert.Equal(2, host.Store.LogCountOf("doc"));
        await WaitUntil(() => { lock (received) return received.Count == 2; });
        lock (received) Assert.Equal([legit, deleteOnly], received);
    }

    [Fact]
    public async Task A_snapshot_with_content_that_was_never_admitted_is_refused()
    {
        await using var host = await StartGuardedAsync();
        await using var alice = host.Connection("alice");
        await alice.StartAsync();
        var baseline = YjsCorpus.Read("editing/000-baseline.state.bin");
        await SeedAsync(alice, "doc", baseline);
        await alice.InvokeAsync<long>("SendUpdate", "doc", NewUpdateId(), YjsCorpus.Read("editing/001-type_5_chars_0.update.bin"));
        var unseen = YjsCorpus.Read("editing/038-final.state.bin");
        var error = await Assert.ThrowsAsync<HubException>(() => alice.InvokeAsync("PushSnapshot", "doc", 1L, unseen, Projections(1, unseen)));
        Assert.Contains("[INVALID_CONTENT]", error.Message);
        Assert.Contains(nameof(ContentRejectionReason.SnapshotExceedsState), error.Message);
        Assert.Equal(baseline, host.Store.SnapshotOf("doc") ?? baseline);
    }

    [Fact]
    public async Task A_snapshot_keeps_the_json_its_state_shows_and_must_show_the_admitted_history()
    {
        await using var host = await StartGuardedAsync();
        await using var alice = host.Connection("alice");
        await alice.StartAsync();
        var baseline = YjsCorpus.Read("editing/000-baseline.state.bin");
        byte[][] updates = [YjsCorpus.Read("editing/001-type_5_chars_0.update.bin"), YjsCorpus.Read("editing/002-type_5_chars_1.update.bin")];
        await SeedAsync(alice, "doc", baseline);
        foreach (var update in updates) await alice.InvokeAsync<long>("SendUpdate", "doc", NewUpdateId(), update);
        var first = YjsUpdateMerger.Merge([baseline, updates[0]], 4L * 1024 * 1024);
        static ProjectionSetDto Forged(long sequence, byte[] state)
        {
            using var json = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"forged\"}]}]}");
            return Projections(sequence, state) with { EditorJson = json.RootElement.Clone() };
        }

        // The state leaves out the second admitted update it claims to cover.
        var error = await Assert.ThrowsAsync<HubException>(() => alice.InvokeAsync("PushSnapshot", "doc", 2L, first, Forged(2, first)));
        Assert.Contains(nameof(ContentRejectionReason.SnapshotMissesHistory), error.Message);

        // Covering the first update, the second follows it in the log.
        await alice.InvokeAsync("PushSnapshot", "doc", 1L, first, Forged(1, first));
        var doc = host.Registry.TryGet("doc")!;
        Assert.Equal(YjsDocumentMaterializer.Builtin.Materialize(first), doc.LastProjection!.EditorJson.GetRawText());
        Assert.Equal(1, doc.SnapshotSeq);

        // The same sequence again moves nothing and stores nothing.
        var revision = doc.SnapshotRevision;
        await alice.InvokeAsync("PushSnapshot", "doc", 1L, first, Forged(1, first));
        Assert.Equal(revision, doc.SnapshotRevision);
    }

    [Fact]
    public async Task A_seed_with_secrets_is_refused_by_default_and_decided_by_the_host_guard()
    {
        var state = YjsCorpus.Read("node-secretReference/004-final.state.bin");
        await using (var host = await StartGuardedAsync())
        {
            await using var alice = host.Connection("alice");
            await alice.StartAsync();
            Assert.True((await alice.InvokeAsync<JoinResult>("JoinDocument", "doc")).IsSnapshotter);
            var error = await Assert.ThrowsAsync<HubException>(() => alice.InvokeAsync("PushSnapshot", "doc", 0L, state, Projections(0, state)));
            Assert.Contains("[INVALID_CONTENT]", error.Message);
            Assert.Null(host.Store.SnapshotOf("doc"));
        }

        var decisions = new List<ReservedNodeRequest>();
        await using (var host = await StartGuardedAsync(new DelegateReservedNodeGuard(request => { lock (decisions) decisions.Add(request); return true; })))
        {
            await using var alice = host.Connection("alice");
            await alice.StartAsync();
            await SeedAsync(alice, "doc", state);
            var request = Assert.Single(decisions);
            Assert.Equal(("doc", "alice"), (request.DocumentId, request.AuthorUserId));
            Assert.Contains("alice", request.PeerUserIds);
            // Every secret element in the state is reported, including one the history already deleted.
            Assert.NotEmpty(request.Changes);
            Assert.All(request.Changes, change => Assert.Equal(("secretReference", true), (change.NodeName, change.IsNewNode)));
        }
    }

    [Fact]
    public async Task Without_the_opt_in_the_relay_stays_opaque()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var alice = host.Connection("alice");
        await alice.StartAsync();
        await SeedAsync(alice, "doc", [1, 2, 3]);
        Assert.Equal(1, await alice.InvokeAsync<long>("SendUpdate", "doc", NewUpdateId(), new byte[] { 9, 9 }));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "condition not met within timeout");
    }

    private sealed class DelegateReservedNodeGuard(Func<ReservedNodeRequest, bool> decide) : IReservedNodeGuard
    {
        public ValueTask<bool> AdmitAsync(ReservedNodeRequest request, CancellationToken ct) => ValueTask.FromResult(decide(request));
    }
}
