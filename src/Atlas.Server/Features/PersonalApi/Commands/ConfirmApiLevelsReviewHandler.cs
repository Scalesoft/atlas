using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Domain;
using Atlas.Server.Features.PersonalApi.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Commands;

public sealed class ConfirmApiLevelsReviewHandler
{
    private readonly ISession m_session;
    private readonly PersonalApiActor m_actor;

    public ConfirmApiLevelsReviewHandler(ISession session, PersonalApiActor actor)
    {
        m_session = session;
        m_actor = actor;
    }

    public async Task<ApiTokensSummary> Handle(ConfirmApiLevelsReview command, CancellationToken ct)
    {
        var actor = await m_actor.LockAsync(ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "API tokeny všech uživatelů spravuje správce wiki.");
        }
        var now = DateTime.UtcNow;
        foreach (var review in await m_session.QueryOver<ApiLevelsReview>().Where(x => x.ConfirmedAt == null).ListAsync(ct))
        {
            review.ConfirmedById = actor.Id;
            review.ConfirmedAt = now;
        }
        await m_session.FlushAsync(ct);
        return await GetApiTokensSummaryHandler.ReadAsync(m_session, ct);
    }
}
