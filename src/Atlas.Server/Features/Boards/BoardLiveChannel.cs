using Atlas.Server.Features.Boards.Queries;
using Atlas.Server.Platform.Live;
using Wolverine;

namespace Atlas.Server.Features.Boards;

/// <summary>The channel <c>board:{boardId}</c> of /hubs/live: confirmed batches (BoardChanged) and presence. Reading needs
/// Reader on the board's space, presence Writer; a page's own board is decided by its page (BoardObjectType).</summary>
public sealed class BoardLiveChannel : ILiveChannel
{
    public const string Prefix = "board";

    private readonly IServiceScopeFactory m_scopes;

    public BoardLiveChannel(IServiceScopeFactory scopes)
    {
        m_scopes = scopes;
    }

    public string Kind => Prefix;

    public static string Name(Guid boardId)
    {
        return Prefix + ":" + boardId.ToString("D");
    }

    public async Task<LiveAccess> AuthorizeAsync(LiveSubscriber subscriber, Guid resourceId, CancellationToken ct)
    {
        await using var scope = m_scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<LiveAccess>(new AuthorizeBoardSubscriber(resourceId, subscriber.UserId, subscriber.SecurityStamp, subscriber.SessionId), ct);
    }
}
