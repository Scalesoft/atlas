using Microsoft.Extensions.Logging.Abstractions;
using Atlas.Markdown.Sync;
using System.Security.Cryptography;
using System.Text.Json;

namespace Atlas.Markdown.Sync.Tests;

public class DocumentRegistryTests
{
    private static DocumentRegistry NewRegistry(IDocumentStore store, MarkdownSyncOptions? options = null)
        => new(store, options ?? new MarkdownSyncOptions(), new CollaboratorColors(), NullLogger<DocumentRegistry>.Instance);

    private static DocSnapshot Snapshot(byte[] state, string markdown, long sequence)
    {
        var hash = Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant();
        var json = JsonSerializer.SerializeToElement(new { type = "doc" });
        return new DocSnapshot(state, sequence, new ProjectionSetDto(sequence, hash, markdown, json, markdown));
    }

    [Fact]
    public void GetOrCreate_returns_the_same_instance_per_id()
    {
        var reg = NewRegistry(new InMemoryDocumentStore());
        var a = reg.GetOrCreate("doc");
        var b = reg.GetOrCreate("doc");
        Assert.Same(a, b);
        Assert.Equal(1, reg.Count);
    }

    [Fact]
    public void WarmKeep_evicts_only_after_the_window_and_not_while_peers_present()
    {
        var opts = new MarkdownSyncOptions { WarmKeep = TimeSpan.FromSeconds(30) };
        var reg = NewRegistry(new InMemoryDocumentStore(), opts);
        var doc = reg.GetOrCreate("doc");
        var t0 = DateTimeOffset.UnixEpoch;

        reg.MarkWarm(doc, t0);
        Assert.Empty(reg.DueForEviction(t0 + TimeSpan.FromSeconds(29)));
        Assert.Single(reg.DueForEviction(t0 + TimeSpan.FromSeconds(30)));

        // A rejoined peer cancels eviction.
        doc.Peers["c1"] = new Peer { ConnectionId = "c1", User = new("u", "u"), CanWrite = true, JoinOrder = 1 };
        reg.ClearWarm(doc);
        Assert.Empty(reg.DueForEviction(t0 + TimeSpan.FromSeconds(60)));

        // A candidate selected from an older sweep must lose eligibility after a fresh leave.
        doc.Peers.Clear();
        reg.MarkWarm(doc, t0 + TimeSpan.FromSeconds(59));
        Assert.False(reg.IsDueForEviction(doc, t0 + TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void ActiveDocumentCount_counts_only_docs_with_peers()
    {
        var reg = NewRegistry(new InMemoryDocumentStore());
        var withPeer = reg.GetOrCreate("live");
        withPeer.Peers["c"] = new Peer { ConnectionId = "c", User = new("u", "u"), CanWrite = true, JoinOrder = 1 };
        reg.GetOrCreate("warm"); // no peers

        Assert.Equal(2, reg.Count);
        Assert.Equal(1, reg.ActiveDocumentCount);
    }

    [Fact]
    public async Task EnsureLoaded_hydrates_stored_state()
    {
        var store = new InMemoryDocumentStore();
        await store.FlushAsync("doc", Snapshot([1, 2, 3], "stored md", 5), default);
        var reg = NewRegistry(store);
        var doc = reg.GetOrCreate("doc");

        await reg.EnsureLoadedAsync(doc, default);

        Assert.True(doc.Loaded);
        Assert.True(doc.Seeded);
        Assert.Equal([1, 2, 3], doc.Snapshot);
        Assert.Equal(5, doc.SnapshotSeq);
    }

    [Fact]
    public async Task EnsureLoaded_with_external_change_discards_stale_state_and_keeps_seed()
    {
        var store = new InMemoryDocumentStore();
        await store.FlushAsync("doc", Snapshot([1, 2, 3], "old", 5), default);
        store.Seed("doc", "external new content");
        store.SetExternalChange("doc", true);
        var reg = NewRegistry(store);
        var doc = reg.GetOrCreate("doc");

        await reg.EnsureLoadedAsync(doc, default);

        Assert.True(doc.Loaded);
        Assert.False(doc.Seeded); // stale state discarded → reseed path
        Assert.Null(doc.Snapshot);
        Assert.Equal("external new content", doc.SeedMarkdown);
    }

    [Fact]
    public async Task ReseedIfExternallyChangedWarm_resets_in_place_when_peerless()
    {
        var store = new InMemoryDocumentStore();
        await store.FlushAsync("doc", Snapshot([1], "v1", 3), default);
        var reg = NewRegistry(store);
        var doc = reg.GetOrCreate("doc");
        await reg.EnsureLoadedAsync(doc, default);
        Assert.True(doc.Seeded);

        // External edit lands while warm (0 peers).
        store.Seed("doc", "external");
        store.SetExternalChange("doc", true);

        var reseeded = await reg.ReseedIfExternallyChangedWarmAsync(doc, default);

        Assert.True(reseeded);
        Assert.Same(doc, reg.GetOrCreate("doc")); // identity preserved (no split-brain)
        Assert.False(doc.Seeded);
        Assert.Equal("external", doc.SeedMarkdown);
    }
}
