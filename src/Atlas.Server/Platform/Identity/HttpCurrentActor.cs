using System.Security.Claims;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Identity;

public sealed class HttpCurrentActor : ICurrentActor
{
    private readonly IHttpContextAccessor m_httpContextAccessor;
    private readonly ISession m_session;
    private UserAccount? m_actor;

    public HttpCurrentActor(IHttpContextAccessor httpContextAccessor, ISession session)
    {
        m_httpContextAccessor = httpContextAccessor;
        m_session = session;
    }

    public async Task<UserAccount> RequireAsync(CancellationToken ct)
    {
        if (m_actor is not null)
        {
            return m_actor;
        }
        var claimed = Claimed();
        var user = await m_session.GetAsync<UserAccount>(claimed.Id, ct);
        if (user is null || !user.IsActive || user.SecurityStamp != claimed.SecurityStamp)
        {
            throw new ApiException(401, "Přihlášení vypršelo.");
        }
        m_actor = user;
        return user;
    }

    public ClaimedActor Claimed()
    {
        var principal = m_httpContextAccessor.HttpContext?.User;
        // Only with no request at all: a request is always its own sign-in, never a job's account that leaked into its flow.
        if (principal is null && ActingAccount.Current is { } acting)
        {
            return acting;
        }
        if (principal is null || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            throw new ApiException(401, "Přihlaste se.");
        }
        return new ClaimedActor(id, principal.FindFirstValue("stamp"), BrowserSessionRevocations.SessionId(principal));
    }
}
