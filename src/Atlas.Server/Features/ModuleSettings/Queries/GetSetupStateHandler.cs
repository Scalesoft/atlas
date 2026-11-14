using Atlas.Server.Features.Setup.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ModuleSettings.Queries;

public sealed class GetSetupStateHandler
{
    private readonly ISession m_session;
    private readonly ModuleSettingsAccess m_access;

    public GetSetupStateHandler(ISession session, ModuleSettingsAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<bool> Handle(GetSetupState query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        return await m_session.QueryOver<ModuleOnboarding>().Where(x => x.ActiveTo == null).RowCountAsync(ct) > 0;
    }
}
