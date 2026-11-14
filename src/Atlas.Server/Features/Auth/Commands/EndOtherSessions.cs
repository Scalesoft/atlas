namespace Atlas.Server.Features.Auth.Commands;

/// <summary>Signs the caller out everywhere else (SEC-A-6): a lost laptop or a stolen cookie no longer waits for a password change
/// (impossible for an account that signs in only through OIDC) or an administrator. Returns the account with its new stamp, which
/// the endpoint puts into this session's cookie.</summary>
public sealed record EndOtherSessions;

public sealed class EndOtherSessionsHandler
{
    private readonly ICurrentActor m_currentActor;
    private readonly IAtlasLocks m_locks;
    private readonly IContentAccess m_access;
    private readonly AccessChanges m_accessChanges;
    private readonly NHibernate.ISession m_session;

    public EndOtherSessionsHandler(ICurrentActor currentActor, IAtlasLocks locks, IContentAccess access, AccessChanges accessChanges,
        NHibernate.ISession session)
    {
        m_currentActor = currentActor;
        m_locks = locks;
        m_access = access;
        m_accessChanges = accessChanges;
        m_session = session;
    }

    public async Task<UserAccount> Handle(EndOtherSessions command, CancellationToken ct)
    {
        // The lock order of ChangePasswordHandler, which rotates the same stamp.
        var claimed = await m_currentActor.RequireAsync(ct);
        await m_locks.AcquireContentSharedAsync(ct);
        await m_locks.LockAccountForUpdateAsync(claimed.Id, ct);
        var actor = await m_access.LockActorAsync(null, ct);
        // Every cookie and personal API token carries the stamp it was issued with: a new one ends them all but this session.
        actor.SecurityStamp = Guid.NewGuid().ToString("N");
        m_accessChanges.Changed();
        await AccountHistory.RecordAsync(m_session, actor, actor.Id, ct);
        return actor;
    }
}
