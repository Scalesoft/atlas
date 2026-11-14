using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth;

/// <summary>
/// The credential events of the access audit (credential.reset_requested, password_changed, activated, recovered) on the account's
/// current revision, with the actor the caller passes (null for an anonymous activation or recovery). A caller that changed the account
/// records its history first (AccountHistory.RecordAsync), so the event names the revision of that change. Was
/// atlas_record_credential_audit until 3.6 (migration 2026110941).
/// </summary>
public sealed class CredentialAudit
{
    private readonly ISession m_session;

    public CredentialAudit(ISession session)
    {
        m_session = session;
    }

    public Task RecordAsync(Guid? actorId, Guid userId, string action, CancellationToken ct)
        => AccountHistory.RecordCredentialAsync(m_session, userId, action, actorId, ct);
}
