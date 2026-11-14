using Microsoft.AspNetCore.SignalR;

namespace Atlas.Server.Platform.Live;

/// <summary>
/// The channels of /hubs/live (Atlas 3 plan 7.2): resolves a channel name to the module's <see cref="ILiveChannel"/>, sends to
/// the current subscribers of a channel, and revalidates every subscription after an access change commits
/// (<see cref="IAccessChangeObserver"/>, awaited by AccessChanges) and every <see cref="RefreshInterval"/>. A subscription that
/// lost access is removed and its connection told <c>Revoked</c>.
/// </summary>
/// <remarks>Messages a module publishes go through here only after the command's commit (AccessChanges.AfterCommit): a rolled
/// back batch has nothing to announce. What was already handed to SignalR cannot be recalled.</remarks>
public sealed class LiveRelay : IAccessChangeObserver
{
    /// <summary>The periodic revalidation, as the collaboration relay's: it applies grants that simply expire (no commit).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    /// <summary>Decisions a sweep runs at once, each with its own database scope; the house bound of background fan-outs.</summary>
    private const int SweepParallelism = 4;

    private readonly LiveSubscriptions m_subscriptions;
    private readonly IHubContext<LiveHub> m_hub;
    private readonly IReadOnlyDictionary<string, ILiveChannel> m_channels;
    private readonly ILogger<LiveRelay> m_log;
    private readonly Lock m_sweepLock = new();
    // The sweep asked for while one runs, with its channels (null: all), and whether a sweep runs.
    private TaskCompletionSource? m_again;
    private HashSet<string>? m_againChannels;
    private bool m_sweeping;

    public LiveRelay(LiveSubscriptions subscriptions, IHubContext<LiveHub> hub, IEnumerable<ILiveChannel> channels, ILogger<LiveRelay> log)
    {
        m_subscriptions = subscriptions;
        m_hub = hub;
        m_channels = channels.ToDictionary(channel => channel.Kind, StringComparer.Ordinal);
        m_log = log;
    }

    /// <summary>The canonical name of <c>{kind}:{resourceId}</c> (any Guid format, sent back as lowercase "D") and its type;
    /// null for a malformed name or a kind no enabled module registered.</summary>
    public (string Channel, ILiveChannel Type, Guid ResourceId)? Resolve(string? channel)
    {
        var separator = channel?.IndexOf(':') ?? -1;
        if (separator <= 0 || !m_channels.TryGetValue(channel![..separator], out var type) || !Guid.TryParse(channel[(separator + 1)..], out var id))
        {
            return null;
        }
        return (type.Kind + ":" + id.ToString("D"), type, id);
    }

    /// <summary>The channel type of a kind whose names the server derives; null for any other kind.</summary>
    public IDerivedLiveChannel? Derived(string? kind)
    {
        return kind is not null && m_channels.GetValueOrDefault(kind) is IDerivedLiveChannel derived ? derived : null;
    }

    /// <summary>Sends to every current subscriber of the channel but <paramref name="exceptConnectionId"/>; with
    /// <paramref name="writersOnly"/> only to those last authorized to write (a page's editors). With <paramref name="readerPayload"/>
    /// the others receive that instead (a board batch without what its readers may not see).</summary>
    // shortcut: recipients are those of the last decision, so a subscriber that lost access still receives what is published from
    // the access change's commit until the sweep after it decides that subscriber: the rest of a running sweep plus that sweep's
    // own run, and if that run fails, the next periodic one (RefreshInterval later). Authorize recipients per message, or hold
    // messages for subscriptions awaiting a sweep, when that window must close.
    public Task PublishAsync(string channel, string method, object payload, string? exceptConnectionId = null, bool writersOnly = false, object? readerPayload = null)
    {
        var recipients = m_subscriptions.Recipients(channel, exceptConnectionId, writersOnly);
        if (readerPayload is null || writersOnly)
        {
            return Send(recipients, method, payload);
        }
        // Read after the others: a subscriber whose write access changed in between gets the payload of its newer decision.
        var writers = m_subscriptions.Recipients(channel, exceptConnectionId, true);
        return Task.WhenAll(Send(writers, method, payload), Send(recipients.Except(writers).ToArray(), method, readerPayload));
    }

    private Task Send(IReadOnlyList<string> recipients, string method, object payload)
    {
        return recipients.Count == 0 ? Task.CompletedTask : m_hub.Clients.Clients(recipients).SendAsync(method, payload);
    }

    public Task AccessChangedAsync()
    {
        return RefreshAsync(null, CancellationToken.None);
    }

    /// <summary>
    /// Re-authorizes the subscriptions of one channel, or all, and completes once a sweep that started after this call has. Sweeps
    /// never overlap: one asked for while another runs waits for one more run after it (the running one may have read before the
    /// caller's commit), shared by every caller meanwhile and covering all their channels. The sweep itself is not cancelled;
    /// <paramref name="ct"/> only stops the waiting.
    /// </summary>
    public Task RefreshAsync(string? channel, CancellationToken ct)
    {
        TaskCompletionSource again;
        bool start;
        lock (m_sweepLock)
        {
            if (m_again is null)
            {
                m_again = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                m_againChannels = [];
            }
            if (channel is null)
            {
                m_againChannels = null;
            }
            else
            {
                m_againChannels?.Add(channel);
            }
            again = m_again;
            start = !m_sweeping;
            m_sweeping = true;
        }
        if (start)
        {
            _ = SweepWhileAskedAsync();
        }
        return again.Task.WaitAsync(ct);
    }

    private async Task SweepWhileAskedAsync()
    {
        while (true)
        {
            TaskCompletionSource? asked;
            HashSet<string>? channels;
            lock (m_sweepLock)
            {
                (asked, channels) = (m_again, m_againChannels);
                (m_again, m_againChannels) = (null, null);
                m_sweeping = asked is not null;
            }
            if (asked is null)
            {
                return;
            }
            try
            {
                await SweepAsync(channels);
                asked.SetResult();
            }
            catch (Exception ex)
            {
                // Never leaves the relay marked as sweeping: the next request still gets its sweep.
                asked.SetException(ex);
            }
        }
    }

    /// <summary>One decision per account, session proof and channel (the tabs of one browser share it, ILiveChannel decides from
    /// nothing else; it answers both read and write), asked of each channel kind at once for all its resources of that account
    /// and session (<see cref="ILiveChannel.AuthorizeManyAsync"/>), at most <see cref="SweepParallelism"/> at once. Only a decision
    /// revokes: an authorization that fails (the database unreachable for a moment) leaves its subscriptions as they were for the
    /// next sweep. A channel no enabled module resolves any more is revoked.</summary>
    private Task SweepAsync(IReadOnlySet<string>? channels)
    {
        // Before anything is read: a decision of this sweep never replaces a newer one (LiveSubscriptions.Decide).
        var ticket = m_subscriptions.NextTicket();
        var keys = m_subscriptions.Subscriptions(channels)
            .Select(item => (item.Subscriber, item.Channel, Resolved: Resolve(item.Channel)))
            .GroupBy(item => (item.Subscriber.UserId, item.Subscriber.SecurityStamp, item.Subscriber.SessionId, Kind: item.Resolved?.Type.Kind));
        return Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = SweepParallelism }, async (key, _) =>
        {
            var type = key.First().Resolved?.Type;
            IReadOnlyDictionary<Guid, LiveAccess> decisions = new Dictionary<Guid, LiveAccess>();
            if (type is not null)
            {
                try
                {
                    decisions = await type.AuthorizeManyAsync(key.First().Subscriber, key.Select(item => item.Resolved!.Value.ResourceId).Distinct().ToArray(), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    m_log.LogWarning(ex, "Live channel authorization failed for {Kind}; its subscriptions are kept until the next sweep", type.Kind);
                    return;
                }
            }
            foreach (var (each, name, resolved) in key)
            {
                var access = resolved is { } channel ? decisions.GetValueOrDefault(channel.ResourceId, LiveAccess.NotFound) : LiveAccess.NotFound;
                if (m_subscriptions.Decide(each.ConnectionId, name, ticket, access.Allowed ? access.CanWrite : null))
                {
                    await NotifyRevokedAsync(each.ConnectionId, name, resolved);
                }
            }
        });
    }

    private async Task NotifyRevokedAsync(string connectionId, string channel, (string Channel, ILiveChannel Type, Guid ResourceId)? resolved)
    {
        try
        {
            await (resolved is { } known
                ? known.Type.RevokedAsync(m_hub, connectionId, channel, known.ResourceId)
                : m_hub.Clients.Client(connectionId).SendAsync("Revoked", new LiveRevoked(channel)));
        }
        catch (Exception ex)
        {
            // The subscription is gone either way; the client only misses the reason.
            m_log.LogWarning(ex, "Revoked could not be sent for {Channel}", channel);
        }
    }
}
