using Atlas.Server.Features.PersonalApi.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Queries;

public sealed class GetApiAccessHandler
{
    private readonly ISession m_session;
    private readonly PersonalApiActor m_actor;

    public GetApiAccessHandler(ISession session, PersonalApiActor actor)
    {
        m_session = session;
        m_actor = actor;
    }

    public async Task<ApiPermissionDto> Handle(GetApiAccess query, CancellationToken ct)
    {
        var actor = await m_actor.LockAsync(ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "Povolení API spravuje správce wiki.");
        }
        if (await m_session.GetAsync<UserAccount>(query.UserId, ct) is null)
        {
            throw new ApiException(404, "Uživatel nebyl nalezen.");
        }
        return CurrentApiPermission.ToDto(await CurrentApiPermission.LoadAsync(m_session, query.UserId, ct));
    }
}
