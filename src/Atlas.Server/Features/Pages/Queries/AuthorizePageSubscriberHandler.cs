using Atlas.Server.Features.Pages.Objects;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Objects;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Queries;

/// <summary>
/// The decision of the page channel at subscribe and at every revalidation: the page object's access for the handshake account
/// (Read on the page; Write when it may edit it, which alone lets it hear <c>working</c>). Plain reads without content locks,
/// like the board channel's.
/// </summary>
public sealed class AuthorizePageSubscriberHandler
{
    private readonly ISession m_session;
    private readonly PageObjectType m_pages;

    public AuthorizePageSubscriberHandler(ISession session, PageObjectType pages)
    {
        m_session = session;
        m_pages = pages;
    }

    public async Task<LiveAccess> Handle(AuthorizePageSubscriber query, CancellationToken ct)
    {
        var actor = await m_session.GetAsync<UserAccount>(query.UserId, ct);
        if (actor is null || !actor.IsActive || actor.SecurityStamp != query.SecurityStamp
            || query.SessionId is { } session && await BrowserSessionRevocations.IsRevokedAsync(m_session, session, ct))
        {
            return LiveAccess.Forbidden;
        }
        var access = await m_pages.AuthorizeAsync(new ObjectActor(actor), query.PageId, ct);
        return access == ObjectAccess.None ? LiveAccess.NotFound : new LiveAccess(LiveAccessOutcome.Allowed, access == ObjectAccess.Write, 0);
    }
}
