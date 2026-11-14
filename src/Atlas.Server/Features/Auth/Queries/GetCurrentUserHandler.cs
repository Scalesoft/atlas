using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.ModuleSettings.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Queries;

public sealed class GetCurrentUserHandler
{
    private readonly ISession m_session;
    private readonly ICurrentActor m_actor;
    private readonly McpModuleState m_mcp;

    public GetCurrentUserHandler(ISession session, ICurrentActor actor, McpModuleState mcp)
    {
        m_session = session;
        m_actor = actor;
        m_mcp = mcp;
    }

    public async Task<UserDto> Handle(GetCurrentUser query, CancellationToken ct)
    {
        var user = await m_actor.RequireAsync(ct);
        var preference = await m_session.QueryOver<UserAppearancePreference>()
            .Where(x => x.UserId == user.Id && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct);
        var ai = await CurrentAiPreference.LoadAsync(m_session, user.Id, ct);
        // The wiki-level switch decides first; the user can only narrow it. "rules" behaves as "on" until the model layer (phase 4).
        var editorAssist = await CurrentEditorAssist.IsEnabledAsync(m_session, ct) && ai?.EditorSuggestions != "off";
        return new UserDto(user.Id, user.Username, user.DisplayName, await EffectiveWikiAdmin.IsAdminAsync(m_session, user, ct), preference?.Appearance ?? Appearance.Default,
            new UserFeatures(editorAssist, ai is { DisabledSuggestionTypes.Length: > 0 } ? ai.DisabledSuggestionTypes : null)
            {
                Mcp = await m_mcp.IsEnabledAsync(ct)
            })
        {
            ReleaseNotesSeen = await m_session.CreateSQLQuery("""SELECT "SeenVersion" FROM "UserReleaseNotes" WHERE "UserId" = :user""")
                .SetParameter("user", user.Id).UniqueResultAsync<string?>(ct)
        };
    }
}
