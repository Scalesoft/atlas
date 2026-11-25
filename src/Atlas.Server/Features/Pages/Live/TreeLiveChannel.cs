using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Platform.Live;
using Wolverine;

namespace Atlas.Server.Features.Pages.Live;

/// <summary>The channel <c>tree:{spaceId}</c> of /hubs/live: TreeChanged when anything a row of the space's tree shows may have changed
/// (a node created, moved, trashed or restored, a title, a publication, an icon, a verification, a page's own board), whoever wrote it
/// (<see cref="PageChangeListener.TreeChanged"/>). Needs the space in the account's list (the tree's own rule). No sequence and no content:
/// an event only says "read the tree again".</summary>
public sealed class TreeLiveChannel : ILiveChannel
{
    public const string Prefix = "tree";

    private readonly IServiceScopeFactory m_scopes;

    public TreeLiveChannel(IServiceScopeFactory scopes)
    {
        m_scopes = scopes;
    }

    public string Kind => Prefix;

    public static string Name(Guid spaceId)
    {
        return Prefix + ":" + spaceId.ToString("D");
    }

    public async Task<LiveAccess> AuthorizeAsync(LiveSubscriber subscriber, Guid resourceId, CancellationToken ct)
    {
        await using var scope = m_scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<LiveAccess>(new AuthorizeTreeSubscriber(resourceId, subscriber.UserId, subscriber.SecurityStamp, subscriber.SessionId), ct);
    }
}

/// <summary>TreeChanged on <c>tree:{spaceId}</c>: read the space's tree again.</summary>
public sealed record TreeChangedMessage(Guid SpaceId);
