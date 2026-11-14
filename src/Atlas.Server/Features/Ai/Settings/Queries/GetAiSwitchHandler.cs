using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.ModuleSettings;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.Queries;

public sealed class GetAiSwitchHandler
{
    private readonly ISession m_session;
    private readonly ModuleSettingsAccess m_access;

    public GetAiSwitchHandler(ISession session, ModuleSettingsAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<AiSwitchView> Handle(GetAiSwitch query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        return CurrentAiSettings.ToView(await CurrentAiSettings.LoadSwitchAsync(m_session, ct));
    }
}
