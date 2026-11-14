using Atlas.Server.Features.Objects.Queries;
using Microsoft.AspNetCore.SignalR;
using Wolverine;

namespace Atlas.Server.Platform.Live;

/// <summary>
/// The object channel <c>obj:{objectId}</c>: subscribed only by view sessions on behalf of their participants (a client cannot
/// subscribe it directly), decided by the object's type, revalidated by the relay's sweeps in one decision per account, session
/// proof and sweep; a revoked subscription is reported to <see cref="ViewSessions"/>, which tells the client ObjectRevoked.
/// </summary>
public sealed class ObjectLiveChannel : ILiveChannel
{
    public const string Prefix = "obj";

    private readonly IServiceScopeFactory m_scopes;
    private readonly ViewSessions m_views;

    public ObjectLiveChannel(IServiceScopeFactory scopes, ViewSessions views)
    {
        m_scopes = scopes;
        m_views = views;
    }

    public string Kind => Prefix;

    public async Task<LiveAccess> AuthorizeAsync(LiveSubscriber subscriber, Guid resourceId, CancellationToken ct)
    {
        return (await AuthorizeAsync(m_scopes, subscriber, [resourceId], ct))[resourceId];
    }

    public Task<IReadOnlyDictionary<Guid, LiveAccess>> AuthorizeManyAsync(LiveSubscriber subscriber, IReadOnlyCollection<Guid> resourceIds, CancellationToken ct)
    {
        return AuthorizeAsync(m_scopes, subscriber, resourceIds, ct);
    }

    public Task RevokedAsync(IHubContext<LiveHub> hub, string connectionId, string channel, Guid resourceId)
    {
        return m_views.RevokedAsync(connectionId, resourceId);
    }

    /// <summary>One transaction for all the objects, as the account the connection authenticated with.</summary>
    internal static async Task<IReadOnlyDictionary<Guid, LiveAccess>> AuthorizeAsync(IServiceScopeFactory scopes, LiveSubscriber subscriber,
        IReadOnlyCollection<Guid> objectIds, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var decided = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<ObjectViewerAccess>(
            new AuthorizeObjectViewer(subscriber.UserId, subscriber.SecurityStamp, subscriber.SessionId, objectIds.ToArray()), ct);
        return decided.Access;
    }
}
