using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>COL2-4: the relay snapshots on its own when no client does, so the pending log never fills up and nothing is lost.</summary>
public class ServerCompactionTests
{
    // One real editor session: typing, marks, a link, split, delete, align, a table, undo; one update per step.
    private static readonly byte[][] Session = MaterializerCorpus.Cases.Single(entry => entry.Name == "editor-session").Steps
        .Select(step => MaterializerCorpus.Read(step.Payload)).ToArray();

    private static ProjectionSetDto Projections(long sequence, byte[] state)
    {
        using var json = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[]}");
        return new ProjectionSetDto(sequence, Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant(), "", json.RootElement.Clone(), "");
    }

    private static string Materialize(byte[] state, IEnumerable<byte[]> updates)
        => YjsDocumentMaterializer.Builtin.Materialize(state, updates.Select(update => (ReadOnlyMemory<byte>)update));

    [Fact]
    public async Task Without_client_snapshots_the_relay_compacts_the_log_and_a_joiner_sees_every_update()
    {
        await using var host = await HubTestHost.StartAsync(options => options.ServerCompactionUpdates = 4,
            services: services => services.AddAtlasMarkdownContentGuard());
        await using var alice = host.Connection("alice");
        await alice.StartAsync();
        var baseline = Session[0];
        Assert.True((await alice.InvokeAsync<JoinResult>("JoinDocument", "doc")).IsSnapshotter);
        await alice.InvokeAsync("PushSnapshot", "doc", 0L, baseline, Projections(0, baseline));
        // The elected writer never sends another snapshot: the browser that should is too busy.
        var updates = Session[1..];
        for (var index = 0; index < updates.Length; index++)
            Assert.Equal(index + 1, await alice.InvokeAsync<long>("SendUpdate", "doc", Guid.NewGuid().ToString("D"), updates[index]));
        var expected = Materialize(baseline, updates);

        var doc = host.Registry.TryGet("doc")!;
        await WaitUntil(() => doc.SnapshotSeq > updates.Length - 8 && !doc.CompactionRunning && doc.Snapshot!.SequenceEqual(host.Store.SnapshotOf("doc")!));
        Assert.Equal(host.Store.LogCountOf("doc"), doc.Log.Count);
        // Stored: the merged state with the server's editor JSON, and the log above it.
        Assert.Equal(expected, Materialize(host.Store.SnapshotOf("doc")!, updates.Skip((int)doc.SnapshotSeq)));
        Assert.Equal(YjsDocumentMaterializer.Builtin.Materialize(doc.Snapshot!), doc.LastProjection!.EditorJson.GetRawText());

        await using var bob = host.Connection("bob");
        await bob.StartAsync();
        var joined = await bob.InvokeAsync<JoinResult>("JoinDocument", "doc");
        Assert.Equal(updates.Length, joined.Seq);
        Assert.Equal(expected, Materialize(joined.Snapshot!, joined.Updates));

        // The snapshot the busy client finally sends is older than the relay's: it is dropped, not an error the client must retry.
        var stale = host.Store.SnapshotOf("doc")!;
        await alice.InvokeAsync("PushSnapshot", "doc", 1L, baseline, Projections(1, baseline));
        Assert.Equal(stale, host.Store.SnapshotOf("doc"));
        Assert.Equal(expected, Materialize(doc.Snapshot!, doc.Log.Select(record => record.Update)));
    }

    [Fact]
    public async Task Without_the_content_guard_nothing_is_compacted()
    {
        await using var host = await HubTestHost.StartAsync(options => options.ServerCompactionUpdates = 2);
        await using var alice = host.Connection("alice");
        await alice.StartAsync();
        var baseline = Session[0];
        await alice.InvokeAsync<JoinResult>("JoinDocument", "doc");
        await alice.InvokeAsync("PushSnapshot", "doc", 0L, baseline, Projections(0, baseline));
        foreach (var update in Session[1..7])
            await alice.InvokeAsync<long>("SendUpdate", "doc", Guid.NewGuid().ToString("D"), update);
        await Task.Delay(300);
        Assert.Equal(0, host.Registry.TryGet("doc")!.SnapshotSeq);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(20);
        }
    }
}
