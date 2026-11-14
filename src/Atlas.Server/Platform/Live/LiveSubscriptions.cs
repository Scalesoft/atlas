using System.Collections.Concurrent;

namespace Atlas.Server.Platform.Live;

/// <summary>
/// Who receives what on /hubs/live: connection → its authenticated account and its channels, each with the write access it was
/// last authorized with by the newest decision (<see cref="Decide"/>). SignalR groups are not used: a group neither authorizes
/// nor forgets a revoked member, so every message picks its recipients here, and a subscription the revalidation removes
/// receives nothing more, not even a message that carries only an id. In memory, like the collaboration relay: one active host.
/// </summary>
// shortcut: recipients are found by scanning every connection; index the channels when one host serves thousands of connections.
public sealed class LiveSubscriptions
{
    /// <summary>Presence messages one connection may send per second; the rest are dropped.</summary>
    public const int PresencePerSecond = 30;

    /// <summary>SubscribeAll calls (the server-derived feeds) one connection may make per second; the rest are refused.</summary>
    public const int FeedsPerSecond = 5;

    /// <summary>Open connections one account may hold (one per browser tab).</summary>
    public const int ConnectionsPerAccount = 50;

    private readonly ConcurrentDictionary<string, Connection> m_connections = new(StringComparer.Ordinal);
    private readonly TimeProvider m_clock;
    private long m_tickets;

    public LiveSubscriptions(TimeProvider clock)
    {
        m_clock = clock;
    }

    /// <summary>False past <see cref="ConnectionsPerAccount"/> open connections of the account (one per tab).</summary>
    // shortcut: two handshakes at the same moment may both pass the count; the bound only stops a runaway client.
    public bool Connect(LiveSubscriber subscriber)
    {
        if (m_connections.Values.Count(connection => connection.Subscriber.UserId == subscriber.UserId) >= ConnectionsPerAccount)
        {
            return false;
        }
        m_connections[subscriber.ConnectionId] = new Connection(subscriber);
        return true;
    }

    public void Disconnect(string connectionId)
    {
        m_connections.TryRemove(connectionId, out _);
    }

    public LiveSubscriber? Find(string connectionId)
    {
        return m_connections.TryGetValue(connectionId, out var connection) ? connection.Subscriber : null;
    }

    /// <summary>Adds or updates the subscription; false when the connection is gone.</summary>
    public bool Subscribe(string connectionId, string channel, bool canWrite)
    {
        if (!m_connections.TryGetValue(connectionId, out var connection))
        {
            return false;
        }
        lock (connection)
        {
            // A repeated Subscribe keeps the ticket of the last decision, so no older decision lands before its own.
            connection.Channels[channel] = new Subscription(canWrite, connection.Channels.GetValueOrDefault(channel).Ticket);
        }
        return true;
    }

    /// <summary>True when the subscription existed (so a revocation is announced once).</summary>
    public bool Unsubscribe(string connectionId, string channel)
    {
        if (!m_connections.TryGetValue(connectionId, out var connection))
        {
            return false;
        }
        lock (connection)
        {
            return connection.Channels.Remove(channel);
        }
    }

    /// <summary>Orders decisions: taken before a decision reads anything, so a later ticket has read later.</summary>
    public long NextTicket()
    {
        return Interlocked.Increment(ref m_tickets);
    }

    /// <summary>
    /// A decision for a subscription that still exists, taken at <paramref name="ticket"/> (<see cref="NextTicket"/>): its write
    /// access, or null when it lost access. A decision older than the one applied last is dropped, so a slow one that read
    /// before a newer one can neither restore write access nor revoke what the newer one allowed. True when it removed the
    /// subscription (so a revocation is announced once).
    /// </summary>
    public bool Decide(string connectionId, string channel, long ticket, bool? canWrite)
    {
        if (!m_connections.TryGetValue(connectionId, out var connection))
        {
            return false;
        }
        lock (connection)
        {
            if (!connection.Channels.TryGetValue(channel, out var current) || current.Ticket > ticket)
            {
                return false;
            }
            if (canWrite is { } write)
            {
                connection.Channels[channel] = new Subscription(write, ticket);
                return false;
            }
            return connection.Channels.Remove(channel);
        }
    }

    /// <summary>Null when not subscribed, else the write access last authorized.</summary>
    public bool? CanWrite(string connectionId, string channel)
    {
        if (!m_connections.TryGetValue(connectionId, out var connection))
        {
            return null;
        }
        lock (connection)
        {
            return connection.Channels.TryGetValue(channel, out var subscription) ? subscription.CanWrite : null;
        }
    }

    /// <summary>Counts a presence message against the connection's budget of <see cref="PresencePerSecond"/>; false: drop it.</summary>
    public bool TryTakePresence(string connectionId)
    {
        return TryTake(connectionId, feed: false, PresencePerSecond);
    }

    /// <summary>Counts a SubscribeAll against the connection's budget of <see cref="FeedsPerSecond"/>; false: refuse it.</summary>
    public bool TryTakeFeed(string connectionId)
    {
        return TryTake(connectionId, feed: true, FeedsPerSecond);
    }

    /// <summary>The connection's channels of one kind (<c>{kind}:…</c>).</summary>
    public IReadOnlyList<string> ChannelsOf(string connectionId, string kind)
    {
        if (!m_connections.TryGetValue(connectionId, out var connection))
        {
            return [];
        }
        lock (connection)
        {
            return connection.Channels.Keys.Where(name => name.StartsWith(kind + ":", StringComparison.Ordinal)).ToList();
        }
    }

    private bool TryTake(string connectionId, bool feed, int limit)
    {
        if (!m_connections.TryGetValue(connectionId, out var connection))
        {
            return false;
        }
        var second = m_clock.GetUtcNow().ToUnixTimeSeconds();
        lock (connection)
        {
            ref var window = ref feed ? ref connection.Feeds : ref connection.Presence;
            if (window.Second != second)
            {
                window = new Window(second, 0);
            }
            return ++window.Count <= limit;
        }
    }

    /// <summary>The connections subscribed to the channel now; with <paramref name="writersOnly"/> those that may write.</summary>
    public IReadOnlyList<string> Recipients(string channel, string? exceptConnectionId = null, bool writersOnly = false)
    {
        var recipients = new List<string>();
        foreach (var (id, connection) in m_connections)
        {
            if (id == exceptConnectionId)
            {
                continue;
            }
            lock (connection)
            {
                if (connection.Channels.TryGetValue(channel, out var subscription) && (subscription.CanWrite || !writersOnly))
                {
                    recipients.Add(id);
                }
            }
        }
        return recipients;
    }

    /// <summary>Every subscription, or those of some channels, for a revalidation.</summary>
    public IReadOnlyList<(LiveSubscriber Subscriber, string Channel)> Subscriptions(IReadOnlySet<string>? channels = null)
    {
        var result = new List<(LiveSubscriber, string)>();
        foreach (var connection in m_connections.Values)
        {
            lock (connection)
            {
                result.AddRange(connection.Channels.Keys.Where(name => channels is null || channels.Contains(name)).Select(name => (connection.Subscriber, name)));
            }
        }
        return result;
    }

    private sealed class Connection(LiveSubscriber subscriber)
    {
        public LiveSubscriber Subscriber { get; } = subscriber;
        public Dictionary<string, Subscription> Channels { get; } = new(StringComparer.Ordinal);
        // Fields: TryTake counts in place.
        public Window Presence;
        public Window Feeds;
    }

    /// <summary>A per-second budget: the second it counts and how many it counted.</summary>
    private record struct Window(long Second, int Count);

    /// <summary>The write access last decided and the ticket of that decision (0 before the first).</summary>
    private readonly record struct Subscription(bool CanWrite, long Ticket);
}
