using Atlas.Server.Platform.Live;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Queries;

public sealed record AuthorizeTreeSubscriber(Guid SpaceId, Guid UserId, string SecurityStamp, Guid? SessionId);

/// <summary>The decision of the tree channel at subscribe and at every revalidation: whether the handshake account may read the space's
/// tree (PageListing.RequireSpaceAsync, as GET tree decides). Plain reads without content locks, like the page channel's.</summary>
public sealed class AuthorizeTreeSubscriberHandler
{
    private readonly ISession m_session;
    private readonly PageListing m_listing;

    public AuthorizeTreeSubscriberHandler(ISession session, PageListing listing)
    {
        m_session = session;
        m_listing = listing;
    }

    public async Task<LiveAccess> Handle(AuthorizeTreeSubscriber query, CancellationToken ct)
    {
        var actor = await m_session.GetAsync<UserAccount>(query.UserId, ct);
        if (actor is null || !actor.IsActive || actor.SecurityStamp != query.SecurityStamp
            || query.SessionId is { } session && await BrowserSessionRevocations.IsRevokedAsync(m_session, session, ct))
        {
            return LiveAccess.Forbidden;
        }
        try
        {
            await m_listing.RequireSpaceAsync(actor, query.SpaceId, ct);
        }
        catch (ApiException error) when (error.Status == 404)
        {
            return LiveAccess.NotFound;
        }
        return new LiveAccess(LiveAccessOutcome.Allowed, false, 0);
    }
}
