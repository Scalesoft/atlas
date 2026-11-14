using System.Security.Claims;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

/// <summary>Durable sign-out facts. A session stays revoked even after sliding cookie renewal or a server restart.</summary>
public static class BrowserSessionRevocations
{
    public static Guid? SessionId(ClaimsPrincipal? principal)
    {
        return Guid.TryParse(principal?.FindFirstValue("atlas_session"), out var id) ? id : null;
    }

    public static Task<bool> IsRevokedAsync(ISession session, Guid sessionId, CancellationToken ct)
    {
        return session.CreateSQLQuery("""
            SELECT EXISTS (SELECT 1 FROM "RevokedBrowserSession" WHERE "Id" = :id)
            """).SetParameter("id", sessionId).UniqueResultAsync<bool>(ct);
    }
}
