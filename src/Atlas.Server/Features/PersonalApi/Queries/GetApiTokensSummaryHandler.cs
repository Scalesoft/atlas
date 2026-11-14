using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Queries;

public sealed class GetApiTokensSummaryHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;

    public GetApiTokensSummaryHandler(ISession session, IContentAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<ApiTokensSummary> Handle(GetApiTokensSummary query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(null, ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "API tokeny všech uživatelů spravuje správce wiki.");
        }
        return await ReadAsync(m_session, ct);
    }

    internal static async Task<ApiTokensSummary> ReadAsync(ISession session, CancellationToken ct)
    {
        var pending = await session.QueryOver<ApiLevelsReview>().Where(x => x.ConfirmedAt == null).RowCountAsync(ct);
        return new ApiTokensSummary(pending > 0);
    }
}
