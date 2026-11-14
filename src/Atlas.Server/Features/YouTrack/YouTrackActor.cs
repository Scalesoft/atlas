using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.YouTrack;

/// <summary>The unlocked actor check the integration has always used: it does not take the shared content advisory lock that IContentAccess acquires. The YouTrack reads (YouTrackReadRunner) run it in a short transaction before the upstream HTTP call and again in another before they release an answer (after the call, or after a cache hit); a space without the integration and a failed call end after the first.</summary>
public sealed class YouTrackActor
{
    private readonly ISession m_session;
    private readonly ICurrentActor m_currentActor;

    public YouTrackActor(ISession session, ICurrentActor currentActor)
    {
        m_session = session;
        m_currentActor = currentActor;
    }

    public async Task<UserAccount> RequireAsync(bool admin, CancellationToken ct)
    {
        var requested = await m_currentActor.RequireAsync(ct);
        // Captured before the refresh: with HttpCurrentActor, requested and current are the same
        // session-cached instance until RefreshAsync below re-reads the row, so comparing against
        // a copy taken here is the only way a stamp rotated after authentication is ever caught.
        var expectedStamp = requested.SecurityStamp;
        var current = await m_session.GetAsync<UserAccount>(requested.Id, ct);
        if (current is not null)
        {
            await m_session.RefreshAsync(current, ct);
        }
        if (current is null || !current.IsActive || current.SecurityStamp != expectedStamp)
        {
            throw new ApiException(401, "Přihlášení vypršelo.");
        }
        if (admin && !await EffectiveWikiAdmin.IsAdminAsync(m_session, current, ct))
        {
            throw new ApiException(403, "Integraci spravuje správce wiki.");
        }
        return current;
    }
}
