using Atlas.Server.Platform.Live;
using Atlas.Server.Features.Pages.Queries;
using Wolverine;

namespace Atlas.Server.Features.Pages.Live;

/// <summary>The channel <c>page:{pageId}</c> of /hubs/live: PageChanged when the page changes (PageChangeListener). Needs Read on
/// the page; its write access is whether it may edit the page, so only an editor hears <c>working</c>. No sequence: an event
/// only says "read the page again".</summary>
public sealed class PageLiveChannel : ILiveChannel
{
    public const string Prefix = "page";

    private readonly IServiceScopeFactory m_scopes;

    public PageLiveChannel(IServiceScopeFactory scopes)
    {
        m_scopes = scopes;
    }

    public string Kind => Prefix;

    public static string Name(Guid pageId)
    {
        return Prefix + ":" + pageId.ToString("D");
    }

    public async Task<LiveAccess> AuthorizeAsync(LiveSubscriber subscriber, Guid resourceId, CancellationToken ct)
    {
        await using var scope = m_scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<LiveAccess>(new AuthorizePageSubscriber(resourceId, subscriber.UserId, subscriber.SecurityStamp, subscriber.SessionId), ct);
    }
}
