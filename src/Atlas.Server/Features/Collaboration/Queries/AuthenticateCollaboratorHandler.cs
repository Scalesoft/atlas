using Atlas.Server.Features.Collaboration.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Queries;

public sealed class AuthenticateCollaboratorHandler
{
    private readonly ISession m_session;

    public AuthenticateCollaboratorHandler(ISession session)
    {
        m_session = session;
    }

    public async Task<CollaboratorIdentity> Handle(AuthenticateCollaborator query, CancellationToken ct)
    {
        var user = await m_session.GetAsync<UserAccount>(query.UserId, ct);
        if (user is null || !user.IsActive || user.SecurityStamp != query.Stamp
            || query.SessionId is { } sessionId && await BrowserSessionRevocations.IsRevokedAsync(m_session, sessionId, ct))
        {
            throw new ApiException(401, "Přihlášení vypršelo.");
        }
        return new CollaboratorIdentity(user.Id, user.DisplayName, user.SecurityStamp, query.SessionId);
    }
}
