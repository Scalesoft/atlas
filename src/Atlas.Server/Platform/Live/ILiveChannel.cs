using Microsoft.AspNetCore.SignalR;

namespace Atlas.Server.Platform.Live;

/// <summary>
/// A channel type of /hubs/live (<c>{Kind}:{resourceId}</c>), registered by the module that owns the resource type. A disabled
/// module registers none, so subscribing to its channels fails with <c>unsupported</c>.
/// </summary>
public interface ILiveChannel
{
    /// <summary>The channel prefix, e.g. <c>board</c>.</summary>
    string Kind { get; }

    /// <summary>Decides for the account the connection authenticated with, at subscribe and at every revalidation (after an
    /// access change commits and every <see cref="LiveRelay.RefreshInterval"/>). Runs outside any request scope. Decides from the
    /// subscriber's account, stamp and browser session only: a sweep asks once for all connections that share them.</summary>
    Task<LiveAccess> AuthorizeAsync(LiveSubscriber subscriber, Guid resourceId, CancellationToken ct);

    /// <summary>The sweep's decision for every resource of this kind the subscriber's account and session hold; one by one unless
    /// the channel decides them together (objects: one transaction for a whole board).</summary>
    async Task<IReadOnlyDictionary<Guid, LiveAccess>> AuthorizeManyAsync(LiveSubscriber subscriber, IReadOnlyCollection<Guid> resourceIds, CancellationToken ct)
    {
        var decisions = new Dictionary<Guid, LiveAccess>();
        foreach (var id in resourceIds)
        {
            decisions[id] = await AuthorizeAsync(subscriber, id, ct);
        }
        return decisions;
    }

    /// <summary>Tells the connection that the sweep removed its subscription: Revoked with the channel, unless the channel says it
    /// its own way.</summary>
    Task RevokedAsync(IHubContext<LiveHub> hub, string connectionId, string channel, Guid resourceId)
    {
        return hub.Clients.Client(connectionId).SendAsync("Revoked", new LiveRevoked(channel));
    }
}

/// <summary>
/// A channel type whose names the server derives and a client never chooses (the jobs and log feeds of Správa wiki): the hub's
/// SubscribeAll(kind) subscribes every resource <see cref="ResourcesAsync"/> answers, each decided again like any channel and
/// revalidated by the sweeps; Subscribe by name refuses the kind. A revocation is told as Revoked with the kind alone, which the
/// client answers with SubscribeAll again.
/// </summary>
public interface IDerivedLiveChannel : ILiveChannel
{
    /// <summary>The resources of this kind the subscriber's account may hear now; empty when none.</summary>
    Task<IReadOnlyList<Guid>> ResourcesAsync(LiveSubscriber subscriber, CancellationToken ct);

    Task ILiveChannel.RevokedAsync(IHubContext<LiveHub> hub, string connectionId, string channel, Guid resourceId)
    {
        return hub.Clients.Client(connectionId).SendAsync("Revoked", new LiveRevoked(Kind));
    }
}
