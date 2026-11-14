using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.ModuleSettings;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.Queries;

public sealed class GetAiFeaturesHandler
{
    private readonly ISession m_session;
    private readonly ModuleSettingsAccess m_access;

    public GetAiFeaturesHandler(ISession session, ModuleSettingsAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<AiFeaturesView> Handle(GetAiFeatures query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        return CurrentAiSettings.ToView(await CurrentAiSettings.LoadFeaturesAsync(m_session, ct));
    }
}
