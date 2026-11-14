using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi;

/// <summary>The actor lock personal API administration needs. IContentAccess takes FOR SHARE and the shared content
/// advisory lock; every personal API write has always taken FOR UPDATE, so it keeps its own primitive.</summary>
public sealed class PersonalApiActor
{
    private readonly ISession m_session;
    private readonly ICurrentActor m_currentActor;

    public PersonalApiActor(ISession session, ICurrentActor currentActor)
    {
        m_session = session;
        m_currentActor = currentActor;
    }

    /// <summary>Locks the actor row FOR UPDATE and re-reads it, then checks browser-session revocation, so a stamp rotation
    /// or logout committed while the request waited is refused here.</summary>
    public async Task<UserAccount> LockAsync(CancellationToken ct)
    {
        var requested = await m_currentActor.RequireAsync(ct);
        // Captured before the refresh: the request actor is loaded through this same session, so it is the very
        // instance RefreshAsync overwrites and comparing against it afterwards could never fail.
        var expectedStamp = requested.SecurityStamp;
        var browserSessionId = m_currentActor.Claimed().BrowserSessionId;
        var current = await m_session.GetAsync<UserAccount>(requested.Id, LockMode.Upgrade, ct);
        if (current is not null)
        {
            // The identity map may hold a copy read before the lock; only a refresh under it sees the current row.
            await m_session.RefreshAsync(current, ct);
        }
        // A separate statement after the lock sees a logout that committed while this request was waiting.
        if (current is null || !current.IsActive || current.SecurityStamp != expectedStamp
            || browserSessionId is { } sessionId && await BrowserSessionRevocations.IsRevokedAsync(m_session, sessionId, ct))
        {
            throw new ApiException(401, "Přihlášení vypršelo.");
        }
        return current;
    }
}
