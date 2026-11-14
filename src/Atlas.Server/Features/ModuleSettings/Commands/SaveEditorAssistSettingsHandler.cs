using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Domain;
using Atlas.Server.Features.ModuleSettings.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ModuleSettings.Commands;

public sealed class SaveEditorAssistSettingsHandler
{
    private const long SettingsLockKey = 731145015;
    private readonly ISession m_session;
    private readonly ModuleSettingsAccess m_access;
    private readonly IAtlasLocks m_locks;

    public SaveEditorAssistSettingsHandler(ISession session, ModuleSettingsAccess access, IAtlasLocks locks)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
    }

    public async Task<EditorAssistSettingsView> Handle(SaveEditorAssistSettings command, CancellationToken ct)
    {
        var actor = await m_access.RequireWikiAdminAsync(ct);
        await m_locks.AcquireAdvisoryAsync(SettingsLockKey, ct);
        var previous = await CurrentEditorAssist.LoadAsync(m_session, ct);
        AdministrationVersion.Expect(previous?.ActiveFrom, command.Request.ExpectedVersion);
        var now = Temporal.NextBoundary(previous?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (previous is not null)
        {
            previous.ActiveTo = now;
            // One current row: close it before the replacement insert reaches the database.
            await m_session.FlushAsync(ct);
        }
        var saved = new EditorAssistSetting { Id = Guid.NewGuid(), Enabled = command.Request.Enabled, ChangedById = actor.Id, ActiveFrom = now };
        await m_session.SaveAsync(saved, ct);
        return CurrentEditorAssist.ToView(saved);
    }
}
