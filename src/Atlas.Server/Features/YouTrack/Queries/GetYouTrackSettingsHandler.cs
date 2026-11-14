using Microsoft.Extensions.Options;
using Atlas.Server.Features.YouTrack.Contracts;
using Atlas.YouTrack;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.YouTrack.Queries;

public sealed class GetYouTrackSettingsHandler
{
    private readonly ISession m_session;
    private readonly YouTrackActor m_actor;
    private readonly IOptions<YouTrackOptions> m_defaults;

    public GetYouTrackSettingsHandler(ISession session, YouTrackActor actor, IOptions<YouTrackOptions> defaults)
    {
        m_session = session;
        m_actor = actor;
        m_defaults = defaults;
    }

    public async Task<YouTrackSettingsDto> Handle(GetYouTrackSettings query, CancellationToken ct)
    {
        await m_actor.RequireAsync(true, ct);
        return await CurrentYouTrackSettings.ReadAsync(m_session, m_defaults.Value, ct);
    }
}
