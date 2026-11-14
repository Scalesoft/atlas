using Atlas.Server.Features.ModuleSettings.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ModuleSettings.Queries;

public sealed class GetEditorAssistSettingsHandler
{
    private readonly ISession m_session;
    private readonly ModuleSettingsAccess m_access;

    public GetEditorAssistSettingsHandler(ISession session, ModuleSettingsAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task<EditorAssistSettingsView> Handle(GetEditorAssistSettings query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        return CurrentEditorAssist.ToView(await CurrentEditorAssist.LoadAsync(m_session, ct));
    }
}
