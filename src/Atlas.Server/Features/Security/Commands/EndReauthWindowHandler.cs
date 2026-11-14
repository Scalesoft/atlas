using System.Security.Claims;
using Atlas.Server.Features.Security.Reauth;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security.Commands;

/// <summary>
/// Sign-out durably revokes its browser session and ends its re-authentication window. The account lock serializes it with
/// protected operations; after commit, live collaboration revalidates the retained session proof.
/// </summary>
public sealed class EndReauthWindowHandler
{
    private readonly ISession m_session;
    private readonly IHttpContextAccessor m_http;
    private readonly IAtlasLocks m_locks;
    private readonly AccessChanges m_accessChanges;

    public EndReauthWindowHandler(ISession session, IHttpContextAccessor http, IAtlasLocks locks, AccessChanges accessChanges)
    {
        m_session = session;
        m_http = http;
        m_locks = locks;
        m_accessChanges = accessChanges;
    }

    public async Task Handle(EndReauthWindow command, CancellationToken ct)
    {
        var sessionId = ReauthProofs.SessionOf(m_http.HttpContext);
        if (sessionId is null || !Guid.TryParse(m_http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return;
        }
        await m_locks.AcquireContentSharedAsync(ct);
        if (!await m_locks.LockAccountForUpdateAsync(userId, ct))
        {
            return;
        }
        if (Guid.TryParse(sessionId, out var browserSessionId))
        {
            await m_session.CreateSQLQuery("""
                INSERT INTO "RevokedBrowserSession" ("Id", "UserId") VALUES (:id, :user)
                ON CONFLICT ("Id") DO NOTHING
                """).SetParameter("id", browserSessionId).SetParameter("user", userId).ExecuteUpdateAsync(ct);
            m_accessChanges.Changed();
        }
        await m_session.CreateSQLQuery("""
            UPDATE "ReauthChallenge" SET "WindowClosedAt" = now()
            WHERE "UserId" = :user AND "SessionId" = :session AND "VerifiedAt" IS NOT NULL AND "WindowClosedAt" IS NULL
            """).SetParameter("user", userId).SetParameter("session", sessionId).ExecuteUpdateAsync(ct);
    }
}
