using Atlas.Server.Features.Auth.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Queries;

public sealed class GetAiPreferencesHandler
{
    private readonly ISession m_session;
    private readonly ICurrentActor m_actor;

    public GetAiPreferencesHandler(ISession session, ICurrentActor actor)
    {
        m_session = session;
        m_actor = actor;
    }

    public async Task<AiPreferencesView> Handle(GetAiPreferences query, CancellationToken ct)
    {
        var user = await m_actor.RequireAsync(ct);
        return CurrentAiPreference.ToView(await CurrentAiPreference.LoadAsync(m_session, user.Id, ct));
    }
}
