using System.Security.Claims;
using System.Text.Json;
using Atlas.Server.Features.Collaboration.Queries;
using Microsoft.AspNetCore.SignalR;
using Wolverine;

namespace Atlas.Server.Platform.Live;

/// <summary>
/// /hubs/live: the platform's channels on one connection per browser tab (Atlas 3 plan 7.2, boards spec §7), here
/// <c>board:{id}</c> and <c>page:{id}</c>, and the view sessions (F1 plan §2.4: JoinView, LeaveView, SetViewport,
/// SendObjectPresence) whose <c>obj:{id}</c> subscriptions the server holds for the client. Like /hubs/markdown it needs the
/// cookie session and the same origin (AtlasPipeline); the account comes from the handshake, never from the client, and the HTTP
/// context accessor is empty inside hub invocations.
/// Errors are HubException with the code as the message: <c>unsupported</c>, <c>not_found</c>, <c>forbidden</c>,
/// <c>invalid_presence</c>, <c>invalid_viewport</c> (the JS client sees "… HubException: code").
/// </summary>
public sealed class LiveHub : Hub
{
    private readonly LiveSubscriptions m_subscriptions;
    private readonly LiveRelay m_relay;
    private readonly ViewSessions m_views;
    private readonly IServiceScopeFactory m_scopes;

    public LiveHub(LiveSubscriptions subscriptions, LiveRelay relay, ViewSessions views, IServiceScopeFactory scopes)
    {
        m_subscriptions = subscriptions;
        m_relay = relay;
        m_views = views;
        m_scopes = scopes;
    }

    public override async Task OnConnectedAsync()
    {
        var user = Context.GetHttpContext()?.User;
        if (!Guid.TryParse(user?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) || BrowserSessionRevocations.SessionId(user) is not { } session)
        {
            Context.Abort();
            return;
        }
        try
        {
            // The collaboration hub's handshake check: active account, the cookie's stamp, a browser session not revoked.
            await using var scope = m_scopes.CreateAsyncScope();
            var identity = await scope.ServiceProvider.GetRequiredService<IMessageBus>()
                .InvokeAsync<Atlas.Server.Features.Collaboration.Contracts.CollaboratorIdentity>(new AuthenticateCollaborator(id, user!.FindFirstValue("stamp"), session), Context.ConnectionAborted);
            if (!m_subscriptions.Connect(new LiveSubscriber(Context.ConnectionId, identity.Id, identity.DisplayName, identity.SecurityStamp, identity.SessionId)))
            {
                // Past LiveSubscriptions.ConnectionsPerAccount: a runaway client, not a person with many tabs.
                Context.Abort();
                return;
            }
        }
        catch (ApiException ex) when (ex.Status == 401)
        {
            Context.Abort();
            return;
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await m_views.DisconnectAsync(Context.ConnectionId);
        m_subscriptions.Disconnect(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public Task<LiveSubscribed> Subscribe(string channel)
    {
        return SubscribeAsync(Subscriber(), ClientChannel(channel) ?? throw new HubException("unsupported"));
    }

    /// <summary>
    /// Every channel of a server-derived kind (<see cref="IDerivedLiveChannel"/>: <c>jobs</c>, <c>logs</c>) the account may hear,
    /// in place of the connection's earlier ones of that kind; the client names only the kind, never a resource. Refused as
    /// <c>forbidden</c> when there is none, <c>rate_limited</c> past <see cref="LiveSubscriptions.FeedsPerSecond"/>.
    /// </summary>
    public async Task<LiveSubscribed> SubscribeAll(string kind)
    {
        var subscriber = Subscriber();
        if (!m_subscriptions.TryTakeFeed(Context.ConnectionId))
        {
            throw new HubException("rate_limited");
        }
        var type = m_relay.Derived(kind) ?? throw new HubException("unsupported");
        UnsubscribeAll(kind);
        // Two decisions whatever the number of channels: the derivation, then all of them together once subscribed (a change committed
        // in between is decided by the second, which its ticket orders against the sweeps).
        var resources = await type.ResourcesAsync(subscriber, Context.ConnectionAborted);
        if (resources.Count == 0)
        {
            throw new HubException("forbidden");
        }
        var channels = resources.Distinct().Select(id => (Name: type.Kind + ":" + id.ToString("D"), Id: id)).ToList();
        foreach (var channel in channels)
        {
            m_subscriptions.Subscribe(Context.ConnectionId, channel.Name, false);
        }
        var ticket = m_subscriptions.NextTicket();
        IReadOnlyDictionary<Guid, LiveAccess> decisions;
        try
        {
            decisions = await type.AuthorizeManyAsync(subscriber, resources, Context.ConnectionAborted);
        }
        catch
        {
            UnsubscribeAll(kind);
            throw;
        }
        var allowed = 0;
        foreach (var channel in channels)
        {
            var access = decisions.GetValueOrDefault(channel.Id, LiveAccess.NotFound);
            allowed += access.Allowed ? 1 : 0;
            m_subscriptions.Decide(Context.ConnectionId, channel.Name, ticket, access.Allowed ? access.CanWrite : null);
        }
        if (allowed == 0)
        {
            UnsubscribeAll(kind);
            throw new HubException("forbidden");
        }
        return new LiveSubscribed(0);
    }

    public void UnsubscribeAll(string kind)
    {
        if (m_relay.Derived(kind) is { } type)
        {
            foreach (var channel in m_subscriptions.ChannelsOf(Context.ConnectionId, type.Kind))
            {
                m_subscriptions.Unsubscribe(Context.ConnectionId, channel);
            }
        }
    }

    private async Task<LiveSubscribed> SubscribeAsync(LiveSubscriber subscriber, (string Channel, ILiveChannel Type, Guid ResourceId) resolved)
    {
        Require(await resolved.Type.AuthorizeAsync(subscriber, resolved.ResourceId, Context.ConnectionAborted));
        m_subscriptions.Subscribe(Context.ConnectionId, resolved.Channel, false);
        // Decided again once subscribed: a change committed between the first decision and the subscription was not delivered,
        // so the client is given the sequence that covers it, and an access lost in between is not left in place. Its ticket orders
        // it against the sweeps (LiveSubscriptions.Decide).
        var ticket = m_subscriptions.NextTicket();
        LiveAccess access;
        try
        {
            access = await resolved.Type.AuthorizeAsync(subscriber, resolved.ResourceId, Context.ConnectionAborted);
            Require(access);
        }
        catch
        {
            m_subscriptions.Unsubscribe(Context.ConnectionId, resolved.Channel);
            throw;
        }
        m_subscriptions.Decide(Context.ConnectionId, resolved.Channel, ticket, access.CanWrite);
        return new LiveSubscribed(access.Sequence);
    }

    public void Unsubscribe(string channel)
    {
        if (ClientChannel(channel) is { } resolved)
        {
            m_subscriptions.Unsubscribe(Context.ConnectionId, resolved.Channel);
        }
    }

    /// <summary>Needs write access as last authorized; past <see cref="LiveSubscriptions.PresencePerSecond"/> a message is dropped.</summary>
    public async Task SendPresence(string channel, LivePresence payload)
    {
        var subscriber = Subscriber();
        var resolved = ClientChannel(channel) ?? throw new HubException("unsupported");
        if (m_subscriptions.CanWrite(Context.ConnectionId, resolved.Channel) != true)
        {
            throw new HubException("forbidden");
        }
        if (!m_subscriptions.TryTakePresence(Context.ConnectionId))
        {
            return;
        }
        if (payload is null || !payload.IsValid())
        {
            throw new HubException("invalid_presence");
        }
        await m_relay.PublishAsync(resolved.Channel, "Presence",
            new LivePresenceMessage(resolved.Channel, subscriber.UserId, subscriber.DisplayName, payload), Context.ConnectionId);
    }

    /// <summary><c>board:{id}</c> or <c>page:{id}</c>: the members the account may read, each with its version.</summary>
    public Task<ViewJoined> JoinView(string viewKey)
    {
        Subscriber();
        return m_views.JoinAsync(Context.ConnectionId, viewKey, Context.ConnectionAborted);
    }

    public Task LeaveView(string viewKey)
    {
        return m_views.LeaveAsync(Context.ConnectionId, viewKey);
    }

    public void SetViewport(string viewKey, Guid[] objectIds)
    {
        m_views.SetViewport(Context.ConnectionId, viewKey, objectIds);
    }

    public Task SendObjectPresence(Guid objectId, JsonElement payload)
    {
        return m_views.SendPresenceAsync(Context.ConnectionId, objectId, payload);
    }

    private LiveSubscriber Subscriber()
    {
        return m_subscriptions.Find(Context.ConnectionId) ?? throw new HubException("forbidden");
    }

    /// <summary>A channel a client may name itself: object channels belong to the view sessions, derived ones to SubscribeAll.</summary>
    private (string Channel, ILiveChannel Type, Guid ResourceId)? ClientChannel(string channel)
    {
        return m_relay.Resolve(channel) is { } resolved && resolved.Type.Kind != ObjectLiveChannel.Prefix && resolved.Type is not IDerivedLiveChannel
            ? resolved : null;
    }

    private static void Require(LiveAccess access)
    {
        if (!access.Allowed)
        {
            throw new HubException(access.Outcome == LiveAccessOutcome.Forbidden ? "forbidden" : "not_found");
        }
    }
}
