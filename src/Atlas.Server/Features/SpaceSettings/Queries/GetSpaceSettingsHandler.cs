using Atlas.Server.Features.SpaceSettings.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceSettings.Queries;

public sealed class GetSpaceSettingsHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;

    public GetSpaceSettingsHandler(ISession session, IContentAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<SpaceSettingsDto> Handle(GetSpaceSettings query, CancellationToken ct)
    {
        await m_access.LockActorAsync(query.SpaceId, ct);
        await m_access.RequireSpaceAsync(query.SpaceId, SpaceRole.Admin, ct);
        return await CurrentSettings.ReadAsync(m_session, query.SpaceId, ct);
    }
}
