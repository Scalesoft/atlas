using Atlas.Server.Features.Auth.Queries;
using Atlas.Server.Features.ModuleSettings.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.AiSuggestions;

/// <summary>
/// E2 and E4 run for the page an author is editing: the actor must be allowed to edit it (EditContent) and have editor assist
/// on (the wiki module "Návrhy při psaní", narrowed by the user's own Profil → AI; <c>features.editorAssist</c>). Anything
/// else is the 404 of a page the actor cannot reach, never a 403 that would confirm the page.
/// </summary>
public sealed class AssistPageGuard(ISession session, PageGate gate)
{
    /// <summary>The page FOR SHARE through the page gate, decided for EditContent and hidden when refused.</summary>
    public async Task<(UserAccount Actor, Page Page)> RequireAsync(Guid pageId, CancellationToken ct)
    {
        var opened = (await gate.ReadAsync(pageId, AccessOperation.EditContent, ct)).RequireHidden();
        // The wiki-level switch decides first; the user can only narrow it ("rules" behaves as "on" until phase 4).
        if (!await CurrentEditorAssist.IsEnabledAsync(session, ct)
            || (await CurrentAiPreference.LoadAsync(session, opened.Actor.Id, ct))?.EditorSuggestions == "off")
        {
            throw Missing();
        }
        return (opened.Actor, opened.Page);
    }

    private static ApiException Missing() => new(404, "Stránka nebyla nalezena.");
}
