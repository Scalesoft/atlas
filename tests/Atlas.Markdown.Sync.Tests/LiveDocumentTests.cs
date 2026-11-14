using Atlas.Markdown.Sync;
using System.Text.Json;
using System.Security.Cryptography;

namespace Atlas.Markdown.Sync.Tests;

public class LiveDocumentTests
{
    private static long PersistAndCommit(LiveDocument document, byte[] update)
    {
        var updateId = Guid.NewGuid();
        var result = new AppendUpdateResult(document.Seq + 1, false);
        document.CommitDurableUpdate(result, updateId, "tester", DateTimeOffset.UnixEpoch, update);
        return result.Sequence;
    }

    private static ProjectionSetDto Projection(long sequence, string markdown, byte[]? state = null)
        => new(sequence, Convert.ToHexString(SHA256.HashData(state ?? [9])).ToLowerInvariant(), markdown,
            JsonSerializer.SerializeToElement(new { type = "doc" }), markdown);

    [Fact]
    public void CommitUpdate_assigns_monotonic_seq()
    {
        var doc = new LiveDocument("d");
        Assert.Equal(1, PersistAndCommit(doc, [1]));
        Assert.Equal(2, PersistAndCommit(doc, [2]));
        Assert.Equal(3, PersistAndCommit(doc, [3]));
        Assert.Equal(3, doc.Seq);
        Assert.Equal(3, doc.Log.Count);
    }

    [Fact]
    public void AdoptSnapshot_truncates_log_up_to_baseline_and_keeps_newer()
    {
        var doc = new LiveDocument("d");
        for (var i = 0; i < 5; i++) PersistAndCommit(doc, [(byte)i]); // seq 1..5

        doc.AdoptSnapshot([9], Projection(3, "rendered md"), basedOnSeq: 3);

        Assert.Equal(3, doc.SnapshotSeq);
        Assert.True(doc.Seeded);
        Assert.True(doc.SnapshotDirty);
        Assert.Equal("rendered md", doc.LastProjection?.Markdown);
        // records with seq <= 3 dropped; 4 and 5 (not yet guaranteed in the snapshot) kept
        Assert.Equal([4L, 5L], doc.Log.Select(r => r.Seq).ToArray());
    }

    [Fact]
    public void AdoptSnapshot_rejects_a_baseline_older_than_the_current_snapshot()
    {
        var doc = new LiveDocument("d");
        for (var i = 0; i < 3; i++) PersistAndCommit(doc, [(byte)i]);
        doc.AdoptSnapshot([9], Projection(3, "current"), basedOnSeq: 3);

        var error = Assert.Throws<InvalidOperationException>(() =>
            doc.AdoptSnapshot([8], Projection(2, "stale"), basedOnSeq: 2));

        Assert.Contains("older", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, doc.SnapshotSeq);
        Assert.Equal("current", doc.LastProjection?.Markdown);
    }

    [Fact]
    public void HydrateFrom_reconstructs_seq_baseline_and_log_tail()
    {
        var doc = new LiveDocument("d");
        var update6 = new LogUpdate(6, Guid.NewGuid(), "alice", DateTimeOffset.UnixEpoch, [2]);
        var update7 = new LogUpdate(7, Guid.NewGuid(), "bob", DateTimeOffset.UnixEpoch, [3]);
        var stored = new StoredDocument(
            Snapshot: [1],
            Updates: [update6, update7],
            Seq: 7,
            SeedMarkdown: null,
            ExternalChange: false,
            RecentUpdateIds: new Dictionary<Guid, long> { [update6.ClientUpdateId] = 6, [update7.ClientUpdateId] = 7 })
        {
            RecentUpdates = new Dictionary<Guid, StoredRecentUpdate>
            {
                [update6.ClientUpdateId] = new(6, update6.Update),
                [update7.ClientUpdateId] = new(7, update7.Update),
            },
        };

        doc.HydrateFrom(stored);

        Assert.True(doc.Seeded);
        Assert.Equal(7, doc.Seq);
        Assert.Equal(5, doc.SnapshotSeq); // 7 - 2 loaded updates
        Assert.Equal([6L, 7L], doc.Log.Select(r => r.Seq).ToArray());
        Assert.True(doc.TryGetSequence(update6.ClientUpdateId, out var duplicateSeq));
        Assert.Equal(6, duplicateSeq);
        Assert.True(doc.TryGetRecentUpdate(update6.ClientUpdateId, out var retained));
        Assert.Equal(update6.Update, retained!.Update);
    }

    [Fact]
    public void WriterPeersByAge_excludes_readonly_and_orders_by_join()
    {
        var doc = new LiveDocument("d");
        doc.Peers["c1"] = new Peer
            { ConnectionId = "c1", User = new("u1", "u1"), CanWrite = true, JoinOrder = doc.NextJoinOrder() };
        doc.Peers["ro"] = new Peer
            { ConnectionId = "ro", User = new("u2", "u2"), CanWrite = false, JoinOrder = doc.NextJoinOrder() };
        doc.Peers["c3"] = new Peer
            { ConnectionId = "c3", User = new("u3", "u3"), CanWrite = true, JoinOrder = doc.NextJoinOrder() };

        Assert.Equal(["c1", "c3"], doc.WriterPeersByAge().Select(p => p.ConnectionId).ToArray());
        doc.Peers["c3"].HasWritten = true;
        Assert.Equal(["c3", "c1"], doc.WriterPeersByAge().Select(p => p.ConnectionId).ToArray());
    }

    [Fact]
    public void ResetForReseed_clears_all_crdt_state()
    {
        var doc = new LiveDocument("d");
        PersistAndCommit(doc, [1]);
        doc.AdoptSnapshot([9], Projection(1, "md"), 1);
        doc.SeedMarkdown = "seed";

        doc.ResetForReseed();

        Assert.Null(doc.Snapshot);
        Assert.False(doc.Seeded);
        Assert.False(doc.Loaded);
        Assert.Equal(0, doc.Seq);
        Assert.Equal(0, doc.SnapshotSeq);
        Assert.Empty(doc.Log);
        Assert.Null(doc.SeedMarkdown);
    }

    [Fact]
    public async Task ClearSeedingState_releases_waiters_with_retry_and_clears_flags()
    {
        var doc = new LiveDocument("d") { SeedInProgress = true, SnapshotterConnId = "c1" };
        var w1 = new TaskCompletionSource<bool>();
        var w2 = new TaskCompletionSource<bool>();
        doc.SeedWaiters.Add(w1);
        doc.SeedWaiters.Add(w2);

        doc.ClearSeedingState();

        Assert.False(doc.SeedInProgress);
        Assert.Null(doc.SnapshotterConnId);
        Assert.Empty(doc.SeedWaiters);
        Assert.True(w1.Task.IsCompleted);
        Assert.True(w2.Task.IsCompleted);
        Assert.False(await w1.Task); // "retry" signal
        Assert.False(await w2.Task);
    }

    [Fact]
    public void ApproxStateBytes_sums_snapshot_and_log()
    {
        var doc = new LiveDocument("d");
        PersistAndCommit(doc, new byte[10]);
        PersistAndCommit(doc, new byte[5]);
        doc.AdoptSnapshot(new byte[100], Projection(0, "md", new byte[100]), 0); // keeps both log records (seq > 0)
        Assert.Equal(100 + 10 + 5, doc.ApproxStateBytes());
    }

    [Fact]
    public void Recent_update_replay_window_is_bounded()
    {
        var doc = new LiveDocument("d");
        Guid first = default, last = default;
        for (var index = 0; index <= 4096; index++)
        {
            var updateId = Guid.NewGuid();
            if (index == 0) first = updateId;
            if (index == 4096) last = updateId;
            doc.CommitDurableUpdate(new AppendUpdateResult(index + 1, false), updateId, "tester",
                DateTimeOffset.UnixEpoch, new byte[] { (byte)(index % 251) });
        }

        Assert.Equal(4096, doc.RecentUpdates.Count);
        Assert.False(doc.TryGetSequence(first, out _));
        Assert.True(doc.TryGetRecentUpdate(last, out var retained));
        Assert.Equal(4097, retained!.Sequence);
    }
}
