using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Atlas.Markdown.Sync;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>Snapshot rejection and recovery through the actual SignalR JSON/socket boundary.</summary>
public class SnapshotBoundaryTests
{
    private const string Doc = "snapshot-boundary";
    private const int DocumentLimit = 32;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static IEnumerable<object[]> InvalidPushes()
    {
        string[] cases = ["null-state", "null-projections", "null-markdown", "null-plain-text", "null-hash",
            "json-null", "json-array", "json-wrong-type", "json-content-object", "negative-sequence",
            "future-sequence", "source-mismatch", "wrong-hash", "uppercase-hash", "oversize"];
        foreach (var initial in new[] { true, false })
            foreach (var kind in cases) yield return [kind, initial];
    }

    [Theory]
    [MemberData(nameof(InvalidPushes))]
    public async Task Invalid_snapshot_is_atomic_and_valid_followup_recovers(string kind, bool initial)
    {
        await using var host = await HubTestHost.StartAsync(options =>
        {
            options.AccessRefreshInterval = TimeSpan.FromHours(1);
            options.FlushInterval = TimeSpan.FromHours(1);
            options.MaxDocumentBytes = DocumentLimit;
        });
        await using var writer = host.Connection("writer");
        await using var reader = host.Connection("reader");
        await writer.StartAsync();
        await reader.StartAsync();
        Assert.True((await writer.InvokeAsync<JoinResult>("JoinDocument", Doc).WaitAsync(Timeout)).IsSnapshotter);

        if (!initial)
        {
            await PushAsync(writer, Valid(0, [1]));
            // Retain both an unflushed snapshot revision and a later log record. A failed
            // candidate covering sequence 1 must neither replace state nor truncate the log.
            await PushAsync(writer, Valid(0, [2]));
            Assert.Equal(1, await SendAsync(writer));
        }
        var waitingJoin = reader.InvokeAsync<JoinResult>("JoinDocument", Doc);
        if (initial) await WaitForSeedWaiterAsync(host, reader.ConnectionId!);
        else await waitingJoin.WaitAsync(Timeout);

        var before = await CaptureAsync(host);
        var invalid = Invalid(kind, initial ? 0 : 1);
        await Assert.ThrowsAsync<HubException>(() => PushAsync(writer, invalid));
        Assert.Equal(before, await CaptureAsync(host));
        if (initial)
        {
            // The failed invocation is the processing barrier; seed waiter state is also
            // included in CaptureAsync, so a delayed client continuation cannot hide release.
            Assert.False(waitingJoin.IsCompleted);
            Assert.Null(host.Store.SnapshotOf(Doc));
            Assert.Equal(0, host.Store.FlushCallsOf(Doc));
        }

        var sequence = initial ? 0 : 1;
        byte[] recoveryState = [7, 8, 9];
        await PushAsync(writer, Valid(sequence, recoveryState));
        var joined = await waitingJoin.WaitAsync(Timeout);
        if (initial)
        {
            Assert.False(joined.IsSnapshotter);
            Assert.Equal(recoveryState, joined.Snapshot);
        }
        var received = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.On<string, byte[], long>("ReceiveUpdate", (id, _, seq) =>
        {
            if (id == Doc) received.TrySetResult(seq);
        });
        Assert.Equal(sequence + 1, await SendAsync(writer));
        Assert.Equal(sequence + 1, await received.Task.WaitAsync(Timeout));

        var live = host.Registry.TryGet(Doc)!;
        using var deadline = new CancellationTokenSource(Timeout);
        await live.Gate.WaitAsync(deadline.Token);
        try
        {
            Assert.Equal(recoveryState, live.Snapshot);
            Assert.Equal(sequence, live.SnapshotSeq);
            Assert.Equal(sequence + 1, live.Seq);
            Assert.Single(live.Log);
            Assert.Equal(sequence + 1, live.Log[0].Seq);
            Assert.True(live.Seeded);
            Assert.False(live.SeedInProgress);
        }
        finally { live.Gate.Release(); }
    }

    private sealed record Push(long Sequence, byte[]? State, ProjectionSetDto? Projections);

    private static Push Valid(long sequence, byte[]? state = null)
    {
        state ??= [5];
        using var json = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[]}");
        return new Push(sequence, state, new ProjectionSetDto(sequence,
            Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant(), "text", json.RootElement.Clone(), "text"));
    }

    private static Push Invalid(string kind, long sequence)
    {
        var valid = Valid(sequence);
        var projection = valid.Projections!;
        return kind switch
        {
            "null-state" => valid with { State = null },
            "null-projections" => valid with { Projections = null },
            "null-markdown" => valid with { Projections = projection with { Markdown = null! } },
            "null-plain-text" => valid with { Projections = projection with { PlainText = null! } },
            "null-hash" => valid with { Projections = projection with { StateSha256 = null! } },
            "json-null" => WithJson(valid, "null"),
            "json-array" => WithJson(valid, "[]"),
            "json-wrong-type" => WithJson(valid, "{\"type\":\"paragraph\"}"),
            "json-content-object" => WithJson(valid, "{\"type\":\"doc\",\"content\":{}}"),
            "negative-sequence" => valid with { Sequence = -1, Projections = projection with { SourceSequence = -1 } },
            "future-sequence" => valid with { Sequence = sequence + 1, Projections = projection with { SourceSequence = sequence + 1 } },
            "source-mismatch" => valid with { Projections = projection with { SourceSequence = sequence + 1 } },
            "wrong-hash" => valid with { Projections = projection with { StateSha256 = new string('0', 64) } },
            "uppercase-hash" => valid with { Projections = projection with { StateSha256 = projection.StateSha256.ToUpperInvariant() } },
            "oversize" => Valid(sequence, new byte[DocumentLimit + 1]),
            _ => throw new ArgumentException("Unknown test case.", nameof(kind)),
        };
    }

    private static Push WithJson(Push push, string value)
    {
        using var json = JsonDocument.Parse(value);
        return push with { Projections = push.Projections! with { EditorJson = json.RootElement.Clone() } };
    }

    private static Task PushAsync(HubConnection writer, Push push)
        => writer.InvokeAsync("PushSnapshot", Doc, push.Sequence, push.State, push.Projections).WaitAsync(Timeout);

    private static Task<long> SendAsync(HubConnection writer)
        => writer.InvokeAsync<long>("SendUpdate", Doc, Guid.NewGuid().ToString("D"), new byte[] { 1 }).WaitAsync(Timeout);

    private static async Task WaitForSeedWaiterAsync(HubTestHost host, string connectionId)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        while (true)
        {
            var live = host.Registry.TryGet(Doc)!;
            await live.Gate.WaitAsync(deadline.Token);
            try
            {
                if (live.Peers.ContainsKey(connectionId) && live.SeedWaiters.Count == 1) return;
            }
            finally { live.Gate.Release(); }
            await Task.Delay(10, deadline.Token);
        }
    }

    private static async Task<string> CaptureAsync(HubTestHost host)
    {
        var live = host.Registry.TryGet(Doc)!;
        using var deadline = new CancellationTokenSource(Timeout);
        await live.Gate.WaitAsync(deadline.Token);
        try
        {
            return JsonSerializer.Serialize(new
            {
                live.Snapshot, live.LastProjection, live.SnapshotSeq, live.Seq, live.Log,
                live.SnapshotDirty, live.SnapshotRevision, live.Seeded, live.SeedInProgress,
                live.SeedPersistenceInFlight, live.SeedPersistenceRequiresReload, live.SeedMarkdown,
                live.SnapshotterConnId,
                SeedWaiters = live.SeedWaiters.Select(waiter => waiter.Task.IsCompleted).ToArray(),
                StoredSnapshot = host.Store.SnapshotOf(Doc), StoredMarkdown = host.Store.MarkdownOf(Doc),
                StoredLogCount = host.Store.LogCountOf(Doc), FlushCalls = host.Store.FlushCallsOf(Doc),
                AppendCalls = host.Store.AppendCallsOf(Doc),
            });
        }
        finally { live.Gate.Release(); }
    }
}
