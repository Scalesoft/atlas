using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ModuleSettings.Queries;

/// <summary>The effective state of the editor-assist module for the whole wiki. Never stored means on.</summary>
public static class CurrentEditorAssist
{
    public static Task<EditorAssistSetting?> LoadAsync(ISession session, CancellationToken ct)
        => session.QueryOver<EditorAssistSetting>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync(ct)!;

    public static async Task<bool> IsEnabledAsync(ISession session, CancellationToken ct)
        => (await LoadAsync(session, ct))?.Enabled ?? true;

    public static EditorAssistSettingsView ToView(EditorAssistSetting? row)
        => new(row?.Enabled ?? true, row is null ? null : AdministrationVersion.Format(row.ActiveFrom));
}
