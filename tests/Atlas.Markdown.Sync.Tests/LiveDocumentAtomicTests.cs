using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Markdown.Sync;

namespace Atlas.Markdown.Sync.Tests;

public class LiveDocumentAtomicTests
{
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static ProjectionSetDto Projection(byte[] state, long seq) => new(seq,
        Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant(), "text",
        JsonSerializer.SerializeToElement(new { type = "doc", content = Array.Empty<object>() }), "text");
    private static StoredDocument Stored() => new([9],
        [new LogUpdate(2, Second, "user", DateTimeOffset.UnixEpoch, [2])], 2, "seed", false,
        new Dictionary<Guid, long> { [First] = 1, [Second] = 2 })
    {
        RecentUpdates = new Dictionary<Guid, StoredRecentUpdate> { [First] = new(1, [1]), [Second] = new(2, [2]) },
    };
    private static LiveDocument Live()
    {
        var doc = new LiveDocument("atomic");
        doc.HydrateFrom(Stored());
        doc.AdoptSnapshot([9], Projection([9], 1), 1);
        return doc;
    }
    private static string Fingerprint(LiveDocument doc) => JsonSerializer.Serialize(new
    {
        doc.Snapshot, doc.LastProjection, doc.Seeded, doc.SnapshotSeq, doc.Seq,
        doc.SnapshotDirty, doc.SnapshotRevision, doc.Log, doc.RecentUpdateIds, doc.RecentUpdates,
    });

    public static IEnumerable<object[]> InvalidStored()
    {
        var stored = Stored();
        yield return ["null", null!];
        yield return ["negative head", stored with { Seq = -1 }];
        yield return ["null log", stored with { Updates = null! }];
        yield return ["null record", stored with { Updates = [null!] }];
        yield return ["null bytes", stored with { Updates = [stored.Updates[0] with { Update = null! }] }];
        yield return ["zero seq", stored with { Updates = [stored.Updates[0] with { Seq = 0 }] }];
        yield return ["future seq", stored with { Updates = [stored.Updates[0] with { Seq = 3 }] }];
        yield return ["head missing", stored with { Seq = 3 }];
        yield return ["gap", stored with { Seq = 4, Updates = [stored.Updates[0], stored.Updates[0] with { Seq = 4 }] }];
        yield return ["reversed", stored with { Updates = [stored.Updates[0], stored.Updates[0] with { Seq = 1 }] }];
        yield return ["null index", stored with { RecentUpdateIds = null! }];
        yield return ["null payload index", stored with { RecentUpdates = null! }];
        foreach (var seq in new long[] { -1, 0, 3 })
            yield return [$"index seq {seq}", stored with { RecentUpdateIds = new Dictionary<Guid, long> { [First] = seq } }];
        yield return ["empty identity", stored with { RecentUpdateIds = new Dictionary<Guid, long> { [Guid.Empty] = 1 } }];
        yield return ["empty log identity", stored with { Updates = [stored.Updates[0] with { ClientUpdateId = Guid.Empty }] }];
        yield return ["payload sequence mismatch", stored with { RecentUpdates = new Dictionary<Guid, StoredRecentUpdate> { [Second] = new(1, [2]) } }];
        yield return ["payload bytes mismatch", stored with { RecentUpdates = new Dictionary<Guid, StoredRecentUpdate> { [Second] = new(2, [99]) } }];
        yield return ["null retained bytes", stored with { RecentUpdates = new Dictionary<Guid, StoredRecentUpdate> { [Second] = new(2, null!) } }];
        yield return ["null retained record", stored with { RecentUpdates = new Dictionary<Guid, StoredRecentUpdate> { [Second] = null! } }];
    }

    [Theory]
    [MemberData(nameof(InvalidStored))]
    public void Rejected_hydration_preserves_entire_live_state_and_can_retry(string name, StoredDocument stored)
    {
        Assert.NotEmpty(name);
        var doc = Live();
        var before = Fingerprint(doc);
        var snapshot = doc.Snapshot;
        var projection = doc.LastProjection;
        Assert.Throws<InvalidDataException>(() => doc.HydrateFrom(stored));
        Assert.Equal(before, Fingerprint(doc));
        Assert.Same(snapshot, doc.Snapshot);
        Assert.Same(projection, doc.LastProjection);
        doc.HydrateFrom(Stored());
        Assert.Equal(2, doc.Seq);
        Assert.Null(doc.LastProjection);
        Assert.False(doc.SnapshotDirty);
    }

    [Theory]
    [InlineData("null-state")]
    [InlineData("null-projection")]
    [InlineData("hash")]
    [InlineData("source")]
    [InlineData("json")]
    [InlineData("markdown")]
    [InlineData("plain-text")]
    [InlineData("negative")]
    [InlineData("stale")]
    [InlineData("future")]
    public void Rejected_snapshot_is_atomic_and_next_valid_snapshot_succeeds(string kind)
    {
        var doc = Live();
        byte[] state = [8];
        long seq = kind switch { "negative" => -1, "stale" => 0, "future" => 3, _ => 2 };
        var projection = Projection(state, seq);
        switch (kind)
        {
            case "null-state": state = null!; break;
            case "null-projection": projection = null!; break;
            case "hash": projection = projection with { StateSha256 = new string('0', 64) }; break;
            case "source": projection = projection with { SourceSequence = 1 }; break;
            case "json": projection = projection with { EditorJson = default }; break;
            case "markdown": projection = projection with { Markdown = null! }; break;
            case "plain-text": projection = projection with { PlainText = null! }; break;
        }
        var before = Fingerprint(doc);
        var error = Record.Exception(() => doc.AdoptSnapshot(state, projection, seq));
        Assert.True(error is InvalidDataException or InvalidOperationException);
        Assert.Equal(before, Fingerprint(doc));
        doc.AdoptSnapshot([8], Projection([8], 2), 2);
        Assert.Empty(doc.Log);
        Assert.Equal(2, doc.SnapshotRevision);
    }

    [Fact]
    public void Hydration_owns_all_buffers_and_collections()
    {
        var stored = Stored();
        var doc = Live();
        doc.HydrateFrom(stored);
        var before = Fingerprint(doc);
        stored.Snapshot![0] = 100;
        stored.Updates[0].Update[0] = 100;
        foreach (var value in stored.RecentUpdates.Values) value.Update[0] = 100;
        ((Dictionary<Guid, long>)stored.RecentUpdateIds).Clear();
        ((Dictionary<Guid, StoredRecentUpdate>)stored.RecentUpdates).Clear();
        Assert.Equal(before, Fingerprint(doc));
    }

    [Fact]
    public void Accepted_snapshot_owns_bytes_and_survives_json_document_disposal()
    {
        var doc = Live();
        byte[] state = [8];
        using (var json = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[]}"))
        {
            doc.AdoptSnapshot(state, Projection(state, 2) with { EditorJson = json.RootElement }, 2);
            state[0] = 100;
        }
        Assert.Equal(new byte[] { 8 }, doc.Snapshot);
        Assert.Equal("doc", doc.LastProjection!.EditorJson.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void Invalid_durable_result_cannot_partially_commit(long sequence, bool emptyIdentity)
    {
        var doc = Live();
        var before = Fingerprint(doc);
        var id = emptyIdentity || sequence == 3 ? Guid.Empty : Guid.NewGuid();
        Assert.NotNull(Record.Exception(() => doc.CommitDurableUpdate(new(sequence, false), id, "user", DateTimeOffset.UnixEpoch, new byte[] { 3 })));
        Assert.Equal(before, Fingerprint(doc));
    }

    [Fact]
    public void Already_disposed_json_is_rejected_before_any_live_mutation()
    {
        var doc = Live();
        var before = Fingerprint(doc);
        var json = JsonDocument.Parse("{\"type\":\"doc\"}");
        var projection = Projection([8], 2) with { EditorJson = json.RootElement };
        json.Dispose();
        Assert.Throws<InvalidDataException>(() => doc.AdoptSnapshot([8], projection, 2));
        Assert.Equal(before, Fingerprint(doc));
    }

    [Fact]
    public void Payload_budget_evicts_old_retry_bytes_without_losing_uncompacted_log()
    {
        var doc = new LiveDocument("budget");
        var ids = new List<Guid>();
        for (var index = 0; index < 9; index++)
        {
            var bytes = new byte[2 * 1024 * 1024];
            bytes[0] = (byte)index;
            var id = Guid.NewGuid(); ids.Add(id);
            doc.CommitDurableUpdate(new(index + 1, false), id, "user", DateTimeOffset.UnixEpoch, bytes);
        }
        Assert.Equal(8, doc.RecentUpdates.Count);
        Assert.False(doc.TryGetSequence(ids[0], out _));
        Assert.Equal(9, doc.Log.Count);
        Assert.Equal(0, doc.Log[0].Update[0]);
        doc.AdoptSnapshot([9], Projection([9], 9), 9);
        doc.HydrateFrom(new StoredDocument(doc.Snapshot, doc.Log.ToArray(), doc.Seq, null, false,
            new Dictionary<Guid, long>(doc.RecentUpdateIds))
        { RecentUpdates = new Dictionary<Guid, StoredRecentUpdate>(doc.RecentUpdates) });
        Assert.Empty(doc.Log);
        Assert.False(doc.TryGetSequence(ids[0], out _));
        Assert.Equal(8, doc.RecentUpdates.Count);
        foreach (var id in ids.Skip(1)) Assert.True(doc.TryGetRecentUpdate(id, out _));
    }

    [Fact]
    public void Deterministic_append_snapshot_reload_model_preserves_tail_and_retry_identity()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            var random = new Random(seed);
            var doc = new LiveDocument("model");
            var updates = new List<LogUpdate>();
            long baseline = 0;
            doc.AdoptSnapshot([9], Projection([9], 0), 0);
            for (var step = 0; step < 50; step++)
            {
                switch (random.Next(3))
                {
                    case 0:
                        var bytes = new byte[8]; random.NextBytes(bytes);
                        var idBytes = new byte[16]; random.NextBytes(idBytes);
                        var record = new LogUpdate(updates.Count + 1, new Guid(idBytes), "user", DateTimeOffset.UnixEpoch, bytes);
                        Assert.True(doc.CommitDurableUpdate(new(record.Seq, false), record.ClientUpdateId, record.UserId, record.ReceivedAtUtc, bytes));
                        updates.Add(record);
                        break;
                    case 1:
                        baseline = random.NextInt64(baseline, doc.Seq + 1);
                        doc.AdoptSnapshot([9], Projection([9], baseline), baseline);
                        break;
                    case 2:
                        doc.HydrateFrom(new StoredDocument(doc.Snapshot, doc.Log.ToArray(), doc.Seq, null, false,
                            new Dictionary<Guid, long>(doc.RecentUpdateIds))
                        { RecentUpdates = new Dictionary<Guid, StoredRecentUpdate>(doc.RecentUpdates) });
                        break;
                }
                Assert.Equal(updates.Count, doc.Seq);
                Assert.Equal(baseline, doc.SnapshotSeq);
                Assert.Equal(updates.Where(x => x.Seq > baseline).Select(x => x.Seq), doc.Log.Select(x => x.Seq));
                foreach (var update in updates)
                {
                    Assert.True(doc.TryGetRecentUpdate(update.ClientUpdateId, out var retained));
                    Assert.Equal(update.Update, retained!.Update);
                    Assert.Equal(update.Seq, retained.Sequence);
                    Assert.False(doc.CommitDurableUpdate(new(update.Seq, true), update.ClientUpdateId, update.UserId, update.ReceivedAtUtc, update.Update));
                }
            }
        }
    }
}
