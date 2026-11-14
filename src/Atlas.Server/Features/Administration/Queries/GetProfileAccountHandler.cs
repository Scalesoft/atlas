using Atlas.Server.Features.Administration.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Queries;

public sealed class GetProfileAccountHandler
{
    private readonly ISession m_session;
    private readonly AdministrationAccess m_access;

    public GetProfileAccountHandler(ISession session, AdministrationAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<ProfileAccount> Handle(GetProfileAccount query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(ct);
        return await ProfileAccountReader.ReadAsync(m_session, actor.Id, query.AuthTime, query.AuthenticationMethod, ct);
    }
}
