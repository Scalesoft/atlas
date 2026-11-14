using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Objects;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Queries;

/// <summary>
/// The decision of the live channel at subscribe and at every revalidation: the board object's access for the handshake account,
/// and its sequence. Plain reads without content locks, like the collaboration relay's AuthorizeCollaborator: it decides for the
/// handshake account, outside any request, and holds nothing.
/// </summary>
public sealed class AuthorizeBoardSubscriberHandler
{
    private readonly ISession m_session;
    private readonly BoardObjectType m_boards;

    public AuthorizeBoardSubscriberHandler(ISession session, BoardObjectType boards)
    {
        m_session = session;
        m_boards = boards;
    }

    public async Task<LiveAccess> Handle(AuthorizeBoardSubscriber query, CancellationToken ct)
    {
        var actor = await m_session.GetAsync<UserAccount>(query.UserId, ct);
        if (actor is null || !actor.IsActive || actor.SecurityStamp != query.SecurityStamp
            || query.SessionId is { } session && await BrowserSessionRevocations.IsRevokedAsync(m_session, session, ct))
        {
            return LiveAccess.Forbidden;
        }
        var access = await m_boards.AuthorizeAsync(new ObjectActor(actor), query.BoardId, ct);
        // The type has loaded the board into this session.
        return access == ObjectAccess.None ? LiveAccess.NotFound
            : new LiveAccess(LiveAccessOutcome.Allowed, access == ObjectAccess.Write, (await m_session.GetAsync<Board>(query.BoardId, ct))!.Sequence);
    }
}
