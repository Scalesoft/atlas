using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Atlas.Markdown.Sync;

namespace Atlas.Markdown.Sync.Tests;

public class SnapshotHostOwnershipTests
{
    private const string Doc = "host-owned-snapshot";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static ProjectionSetDto Projection(byte[] bytes)
        => new(0, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "seed",
            JsonSerializer.SerializeToElement(new { type = "doc" }), "seed");

    [Fact]
    public async Task Host_store_mutating_its_snapshot_after_persistence_cannot_damage_seed_or_waiting_join()
    {
        var store = new MutatingFlushStore();
        await using var host = await HubTestHost.StartAsync(options =>
        {
            options.FlushInterval = TimeSpan.FromHours(1);
            options.AccessRefreshInterval = TimeSpan.FromHours(1);
        }, store: store);
        await using var writer = host.Connection("writer");
        await using var reader = host.Connection("reader");
        await writer.StartAsync();
        await reader.StartAsync();
        await writer.InvokeAsync<JoinResult>("JoinDocument", Doc).WaitAsync(Timeout);
        var joining = reader.InvokeAsync<JoinResult>("JoinDocument", Doc);
        using (var deadline = new CancellationTokenSource(Timeout))
        {
            while (true)
            {
                var live = host.Registry.TryGet(Doc)!;
                await live.Gate.WaitAsync(deadline.Token);
                try { if (live.SeedWaiters.Count == 1) break; }
                finally { live.Gate.Release(); }
                await Task.Delay(10, deadline.Token);
            }
        }

        byte[] original = [1, 2, 3];
        var push = writer.InvokeAsync("PushSnapshot", Doc, 0L, original, Projection(original));
        try
        {
            await store.Persisted.Task.WaitAsync(Timeout);
            Assert.False(joining.IsCompleted);
            Assert.Equal(original, store.Inner.SnapshotOf(Doc));
            store.ReleaseReturn.TrySetResult();
            await push.WaitAsync(Timeout);
            var joined = await joining.WaitAsync(Timeout);
            Assert.Equal(original, joined.Snapshot);
            Assert.Equal(original, host.Registry.TryGet(Doc)!.Snapshot);
            Assert.Equal(original, store.Inner.SnapshotOf(Doc));
            Assert.Equal(255, store.Received!.YjsState[0]);
            Assert.False(host.Registry.TryGet(Doc)!.SeedInProgress);
            Assert.False(host.Registry.TryGet(Doc)!.SeedPersistenceInFlight);

            // The store can also retain its argument. Later buffer reuse must stay local.
            store.Received.YjsState[1] = 254;
            Assert.Equal(original, host.Registry.TryGet(Doc)!.Snapshot);
            var received = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            reader.On<string, byte[], long>("ReceiveUpdate", (_, _, seq) => received.TrySetResult(seq));
            Assert.Equal(1, await writer.InvokeAsync<long>("SendUpdate", Doc,
                Guid.NewGuid().ToString("D"), new byte[] { 4 }).WaitAsync(Timeout));
            Assert.Equal(1, await received.Task.WaitAsync(Timeout));
        }
        finally { store.ReleaseReturn.TrySetResult(); }
    }

    [Fact]
    public async Task Failed_registry_reload_preserves_seed_markdown_until_successful_replacement()
    {
        var store = new ReloadStore
        {
            Result = new StoredDocument([1], [], 1, "invalid replacement", false,
                new Dictionary<Guid, long> { [Guid.NewGuid()] = 2 }),
        };
        var registry = new DocumentRegistry(store, new MarkdownSyncOptions(), new CollaboratorColors(), NullLogger<DocumentRegistry>.Instance);
        var live = registry.GetOrCreate(Doc);
        live.SeedMarkdown = "original seed";
        await live.Gate.WaitAsync();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.EnsureLoadedAsync(live, CancellationToken.None));
            Assert.Equal("original seed", live.SeedMarkdown);
            Assert.False(live.Loaded);

            store.Result = new StoredDocument([2], [], 0, "valid replacement", false, new Dictionary<Guid, long>());
            await registry.EnsureLoadedAsync(live, CancellationToken.None);
            Assert.Equal("valid replacement", live.SeedMarkdown);
            Assert.Equal(new byte[] { 2 }, live.Snapshot);
            Assert.True(live.Loaded);
        }
        finally { live.Gate.Release(); }
    }

    [Fact]
    public async Task Empty_update_id_is_rejected_before_host_store_or_live_commit_and_next_write_succeeds()
    {
        await using var host = await HubTestHost.StartAsync(options =>
        {
            options.FlushInterval = TimeSpan.FromHours(1);
            options.AccessRefreshInterval = TimeSpan.FromHours(1);
        });
        await using var writer = host.Connection("writer");
        await writer.StartAsync();
        await writer.InvokeAsync<JoinResult>("JoinDocument", Doc).WaitAsync(Timeout);
        byte[] state = [1];
        await writer.InvokeAsync("PushSnapshot", Doc, 0L, state, Projection(state)).WaitAsync(Timeout);

        await Assert.ThrowsAsync<HubException>(() => writer.InvokeAsync<long>("SendUpdate", Doc,
            Guid.Empty.ToString("D"), new byte[] { 2 }).WaitAsync(Timeout));

        Assert.Equal(0, host.Store.AppendCallsOf(Doc));
        var live = host.Registry.TryGet(Doc)!;
        Assert.Equal(0, live.Seq);
        Assert.Empty(live.Log);
        Assert.Empty(live.RecentUpdateIds);
        Assert.Empty(live.RecentUpdates);
        Assert.Equal(state, live.Snapshot);
        Assert.Equal(1, await writer.InvokeAsync<long>("SendUpdate", Doc,
            Guid.NewGuid().ToString("D"), new byte[] { 3 }).WaitAsync(Timeout));
        Assert.Equal(1, host.Store.AppendCallsOf(Doc));
    }

    private sealed class MutatingFlushStore : IDocumentStore
    {
        public InMemoryDocumentStore Inner { get; } = new();
        public DocSnapshot? Received { get; private set; }
        public TaskCompletionSource Persisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReturn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct) => Inner.LoadAsync(documentId, ct);
        public Task<bool> HasExternalChangeAsync(string documentId, CancellationToken ct) => Inner.HasExternalChangeAsync(documentId, ct);
        public Task<MarkdownDocumentSchema?> BindSchemaAsync(string documentId, MarkdownDocumentSchema? schema, CancellationToken ct)
            => Inner.BindSchemaAsync(documentId, schema, ct);
        public Task<AppendUpdateResult> AppendUpdateAsync(string documentId, Guid clientUpdateId, string userId,
            DateTimeOffset receivedAtUtc, ReadOnlyMemory<byte> update, CancellationToken ct)
            => Inner.AppendUpdateAsync(documentId, clientUpdateId, userId, receivedAtUtc, update, ct);
        public async Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct)
        {
            Received = snapshot;
            // The durable store owns a separate copy; the host subsequently reuses its input.
            var result = await Inner.FlushAsync(documentId, SnapshotValidation.Prepare(snapshot), ct);
            Persisted.TrySetResult();
            await ReleaseReturn.Task.WaitAsync(ct);
            snapshot.YjsState[0] = 255;
            return result;
        }
    }

    private sealed class ReloadStore : IDocumentStore
    {
        public required StoredDocument Result { get; set; }
        public Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct) => Task.FromResult<StoredDocument?>(Result);
        public Task<bool> HasExternalChangeAsync(string documentId, CancellationToken ct) => Task.FromResult(false);
        public Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<AppendUpdateResult> AppendUpdateAsync(string documentId, Guid clientUpdateId, string userId,
            DateTimeOffset receivedAtUtc, ReadOnlyMemory<byte> update, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
