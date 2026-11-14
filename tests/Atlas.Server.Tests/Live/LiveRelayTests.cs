using Atlas.Server.Platform.Live;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Atlas.Server.Tests.Live;

/// <summary>The registry and relay of /hubs/live without a host: who receives a message, revocation and the presence budget.</summary>
public sealed class LiveRelayTests
{
    private static readonly Guid s_resource = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly string s_channel = "test:" + s_resource.ToString("D");

    [Fact]
    public async Task A_revalidation_removes_a_subscriber_that_lost_access_tells_it_once_and_sends_it_nothing_more()
    {
        var (subscriptions, hub, channel, relay) = Relay();
        Connect(subscriptions, "reader", "writer", "failing");
        Assert.All(new[] { "reader", "writer", "failing" }, id => Assert.True(subscriptions.Subscribe(id, s_channel, false)));
        channel.Decide = subscriber => subscriber.ConnectionId switch
        {
            "reader" => LiveAccess.NotFound,
            "writer" => new LiveAccess(LiveAccessOutcome.Allowed, true, 4),
            _ => throw new InvalidOperationException("database unavailable")
        };

        await relay.RefreshAsync(null, CancellationToken.None);

        // Lost access revokes; the one still allowed gets its new write access. A decision that failed (a database hiccup) is no
        // decision: that subscription stays as it was, and the next sweep decides it.
        Assert.Equal(("reader", "Revoked", (object)new LiveRevoked(s_channel)), (Assert.Single(Assert.Single(hub.Sent).Connections), hub.Sent[0].Method, hub.Sent[0].Payload!));
        Assert.Null(subscriptions.CanWrite("reader", s_channel));
        Assert.True(subscriptions.CanWrite("writer", s_channel));
        Assert.False(subscriptions.CanWrite("failing", s_channel));

        await relay.PublishAsync(s_channel, "BoardChanged", "after");
        Assert.Equal(new[] { "failing", "writer" }, hub.Sent[^1].Connections.Order());
        channel.Decide = subscriber => subscriber.ConnectionId == "failing" ? LiveAccess.NotFound : new LiveAccess(LiveAccessOutcome.Allowed, true, 4);
        await relay.RefreshAsync(null, CancellationToken.None);
        Assert.Equal(("failing", "Revoked"), (Assert.Single(hub.Sent[^1].Connections), hub.Sent[^1].Method));
        Assert.Equal(3, hub.Sent.Count);
        // Without subscribers there is nothing to send at all.
        await relay.PublishAsync("test:" + Guid.NewGuid(), "BoardChanged", "nobody");
        Assert.Equal(3, hub.Sent.Count);
    }

    /// <summary>A sweep asked for while one runs does not start a second one beside it: it waits for one more run after the
    /// running sweep (which may have read before the caller's commit), shared by everyone who asked meanwhile. Within a sweep the
    /// tabs of one browser session share one decision per channel.</summary>
    [Fact]
    public async Task Sweeps_asked_for_while_one_runs_coalesce_and_decide_each_account_session_and_channel_once()
    {
        var (subscriptions, _, channel, relay) = Relay();
        var account = Guid.NewGuid();
        var session = Guid.NewGuid();
        foreach (var tab in new[] { "tab-1", "tab-2", "tab-3" })
        {
            subscriptions.Connect(new LiveSubscriber(tab, account, "Tabs", "stamp", session));
            subscriptions.Subscribe(tab, s_channel, true);
        }
        Connect(subscriptions, "other");
        subscriptions.Subscribe("other", s_channel, false);
        var slow = new TaskCompletionSource();
        channel.Gate = slow.Task;

        var running = relay.RefreshAsync(null, CancellationToken.None);
        await channel.WaitForCallsAsync(2);
        var asked = new[] { relay.RefreshAsync(null, CancellationToken.None), relay.RefreshAsync(s_channel, CancellationToken.None) };
        Assert.Equal(2, channel.Calls);
        Assert.DoesNotContain(asked, task => task.IsCompleted);
        channel.Gate = Task.CompletedTask;
        slow.SetResult();
        await running;
        await Task.WhenAll(asked);

        // Two keys (the three tabs, the other account), decided by the running sweep and by the one run both requests share.
        Assert.Equal(4, channel.Calls);
        Assert.All(new[] { "tab-1", "tab-2", "tab-3" }, tab => Assert.False(subscriptions.CanWrite(tab, s_channel)));
    }

    /// <summary>Sweeps never overlap, but the hub decides at Subscribe beside them: a decision that read the database earlier
    /// and answers later is dropped, so it can neither restore write access nor revoke what a newer decision allowed.</summary>
    [Fact]
    public async Task A_slow_old_decision_cannot_restore_write_access_or_revoke_a_subscription_a_newer_decision_settled()
    {
        var (subscriptions, hub, channel, relay) = Relay();
        Connect(subscriptions, "writer", "reader");
        subscriptions.Subscribe("writer", s_channel, true);
        subscriptions.Subscribe("reader", s_channel, false);
        // What the old sweep read: the writer may still write, the reader had lost the board.
        channel.Decide = subscriber => subscriber.ConnectionId == "writer" ? new LiveAccess(LiveAccessOutcome.Allowed, true, 1) : LiveAccess.NotFound;
        var slow = new TaskCompletionSource();
        channel.Gate = slow.Task;
        var old = relay.RefreshAsync(null, CancellationToken.None);
        await channel.WaitForCallsAsync(2);

        // Newer decisions, as Subscribe takes them: the writer was made a reader, the reader got the board back.
        Assert.False(subscriptions.Decide("writer", s_channel, subscriptions.NextTicket(), false));
        Assert.False(subscriptions.Decide("reader", s_channel, subscriptions.NextTicket(), false));
        slow.SetResult();
        await old;

        Assert.False(subscriptions.CanWrite("writer", s_channel));
        Assert.False(subscriptions.CanWrite("reader", s_channel));
        Assert.Empty(hub.Sent);
        // A decision taken after them applies as usual.
        Assert.True(subscriptions.Decide("reader", s_channel, subscriptions.NextTicket(), null));
        Assert.Null(subscriptions.CanWrite("reader", s_channel));
    }

    /// <summary>Documents the bounded window of LiveRelay (its shortcut): a batch committed after an access change but published
    /// before the sweep after that change decides a subscriber still reaches it; from the decision on, nothing does.</summary>
    [Fact]
    public async Task A_message_published_before_the_sweep_decides_a_revoked_subscriber_still_reaches_it_and_none_after()
    {
        var (subscriptions, hub, channel, relay) = Relay();
        Connect(subscriptions, "revoked");
        subscriptions.Subscribe("revoked", s_channel, false);
        // The revoke has committed and its sweep has read the decision, but not applied it yet.
        channel.Decide = _ => LiveAccess.NotFound;
        var slow = new TaskCompletionSource();
        channel.Gate = slow.Task;
        var sweep = relay.AccessChangedAsync();
        await channel.WaitForCallsAsync(1);

        await relay.PublishAsync(s_channel, "BoardChanged", "committed after the revoke");
        Assert.Equal("revoked", Assert.Single(Assert.Single(hub.Sent).Connections));
        slow.SetResult();
        await sweep;
        await relay.PublishAsync(s_channel, "BoardChanged", "committed after the sweep");
        Assert.Equal(["BoardChanged", "Revoked"], hub.Sent.Select(sent => sent.Method));
    }

    /// <summary>A sweep asks a channel kind once for all the resources one account and session hold (a board's objects in one
    /// transaction), and a channel that tells a revocation its own way (ObjectRevoked) is asked to.</summary>
    [Fact]
    public async Task A_sweep_decides_all_resources_of_a_kind_for_one_account_at_once_and_lets_the_channel_tell_the_revocation()
    {
        var subscriptions = new LiveSubscriptions(TimeProvider.System);
        var hub = new RecordingHubContext();
        var batch = new BatchChannel();
        var relay = new LiveRelay(subscriptions, hub, [batch], NullLogger<LiveRelay>.Instance);
        var (kept, lost) = (Guid.NewGuid(), Guid.NewGuid());
        Connect(subscriptions, "tab");
        subscriptions.Subscribe("tab", "batch:" + kept, false);
        subscriptions.Subscribe("tab", "batch:" + lost, false);
        batch.Allowed = [kept];

        await relay.RefreshAsync(null, CancellationToken.None);

        Assert.Equal(new[] { kept, lost }.Order(), Assert.Single(batch.Asked).Order());
        Assert.False(subscriptions.CanWrite("tab", "batch:" + kept));
        Assert.Null(subscriptions.CanWrite("tab", "batch:" + lost));
        Assert.Equal(("tab", lost), Assert.Single(batch.Revoked));
        Assert.Empty(hub.Sent);
    }

    [Fact]
    public async Task A_message_goes_to_the_subscribers_of_its_channel_only_and_never_back_to_its_sender()
    {
        var (subscriptions, hub, _, relay) = Relay();
        Connect(subscriptions, "a", "b", "c");
        subscriptions.Subscribe("a", s_channel, true);
        subscriptions.Subscribe("b", s_channel, false);
        subscriptions.Subscribe("c", "test:" + Guid.NewGuid(), false);
        Assert.False(subscriptions.Subscribe("gone", s_channel, false));

        await relay.PublishAsync(s_channel, "Presence", "moved", "a");
        Assert.Equal("b", Assert.Single(Assert.Single(hub.Sent).Connections));

        subscriptions.Unsubscribe("b", s_channel);
        subscriptions.Disconnect("a");
        Assert.Empty(subscriptions.Recipients(s_channel));
        Assert.Null(subscriptions.Find("a"));
    }

    [Fact]
    public void Presence_is_limited_to_thirty_messages_a_second_for_each_connection()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
        var subscriptions = new LiveSubscriptions(clock);
        Connect(subscriptions, "a", "b");
        Assert.Equal(LiveSubscriptions.PresencePerSecond, Enumerable.Range(0, 45).Count(_ => subscriptions.TryTakePresence("a")));
        Assert.True(subscriptions.TryTakePresence("b"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(subscriptions.TryTakePresence("a"));
        Assert.False(subscriptions.TryTakePresence("gone"));
    }

    [Fact]
    public void A_channel_name_resolves_only_for_a_registered_kind_and_comes_back_canonical()
    {
        var (_, _, _, relay) = Relay();
        var resolved = relay.Resolve("test:" + s_resource.ToString("N").ToUpperInvariant());
        Assert.Equal((s_channel, s_resource), (resolved!.Value.Channel, resolved.Value.ResourceId));
        foreach (var unsupported in new[] { "board:" + s_resource, "test:" + "not-a-guid", ":" + s_resource, "test", "", null })
        {
            Assert.Null(relay.Resolve(unsupported));
        }
    }

    [Fact]
    public void Presence_relays_finite_numbers_and_at_most_fifty_dragged_items()
    {
        var drag = new LiveDrag(Guid.NewGuid(), 1, 2, 3, 4);
        Assert.True(new LivePresence(new LivePoint(1, 2), Enumerable.Repeat(drag, LivePresence.MaxDragging).ToArray()).IsValid());
        Assert.True(new LivePresence(null, null).IsValid());
        Assert.False(new LivePresence(null, Enumerable.Repeat(drag, LivePresence.MaxDragging + 1).ToArray()).IsValid());
        Assert.False(new LivePresence(new LivePoint(double.NaN, 0), null).IsValid());
        Assert.False(new LivePresence(null, [drag with { Width = double.PositiveInfinity }]).IsValid());
        Assert.False(new LivePresence(null, [null!]).IsValid());
    }

    private static (LiveSubscriptions, RecordingHubContext, TestChannel, LiveRelay) Relay()
    {
        var subscriptions = new LiveSubscriptions(TimeProvider.System);
        var hub = new RecordingHubContext();
        var channel = new TestChannel();
        return (subscriptions, hub, channel, new LiveRelay(subscriptions, hub, [channel], NullLogger<LiveRelay>.Instance));
    }

    private static void Connect(LiveSubscriptions subscriptions, params string[] connections)
    {
        foreach (var connection in connections)
        {
            subscriptions.Connect(new LiveSubscriber(connection, Guid.NewGuid(), connection, "stamp", null));
        }
    }

    private sealed class BatchChannel : ILiveChannel
    {
        public HashSet<Guid> Allowed { get; set; } = [];
        public List<Guid[]> Asked { get; } = [];
        public List<(string Connection, Guid Resource)> Revoked { get; } = [];

        public string Kind => "batch";

        public Task<LiveAccess> AuthorizeAsync(LiveSubscriber subscriber, Guid resourceId, CancellationToken ct) => throw new InvalidOperationException("Asked one by one.");

        public Task<IReadOnlyDictionary<Guid, LiveAccess>> AuthorizeManyAsync(LiveSubscriber subscriber, IReadOnlyCollection<Guid> resourceIds, CancellationToken ct)
        {
            lock (Asked)
            {
                Asked.Add(resourceIds.ToArray());
            }
            return Task.FromResult<IReadOnlyDictionary<Guid, LiveAccess>>(resourceIds.ToDictionary(id => id,
                id => Allowed.Contains(id) ? new LiveAccess(LiveAccessOutcome.Allowed, false, 0) : LiveAccess.NotFound));
        }

        public Task RevokedAsync(Microsoft.AspNetCore.SignalR.IHubContext<LiveHub> hub, string connectionId, string channel, Guid resourceId)
        {
            lock (Revoked)
            {
                Revoked.Add((connectionId, resourceId));
            }
            return Task.CompletedTask;
        }
    }

    private sealed class TestChannel : ILiveChannel
    {
        private int m_calls;

        public Func<LiveSubscriber, LiveAccess> Decide { get; set; } = _ => new LiveAccess(LiveAccessOutcome.Allowed, false, 0);

        /// <summary>Awaited after the decision is read and before it is answered: a slow database round trip.</summary>
        public Task Gate { get; set; } = Task.CompletedTask;

        public int Calls => Volatile.Read(ref m_calls);

        public string Kind => "test";

        public async Task<LiveAccess> AuthorizeAsync(LiveSubscriber subscriber, Guid resourceId, CancellationToken ct)
        {
            var decision = Decide(subscriber);
            Interlocked.Increment(ref m_calls);
            await Gate;
            return decision;
        }

        public async Task WaitForCallsAsync(int calls)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Calls < calls)
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }
}
