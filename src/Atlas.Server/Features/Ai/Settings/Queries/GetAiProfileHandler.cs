using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.ModuleSettings;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.Queries;

public sealed class GetAiProfileHandler
{
    private readonly ISession m_session;
    private readonly ModuleSettingsAccess m_access;
    private readonly IEmbeddingChangeGuard m_index;

    public GetAiProfileHandler(ISession session, ModuleSettingsAccess access, IEmbeddingChangeGuard index)
    {
        m_session = session;
        m_access = access;
        m_index = index;
    }

    public async Task<AiProfileView> Handle(GetAiProfile query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        var kind = AiProfileRules.RequireKind(query.Kind);
        var assignment = await CurrentAiSettings.LoadAssignmentAsync(m_session, kind, ct);
        var revision = assignment is null ? null : await m_session.GetAsync<AiProfileRevision>(assignment.RevisionId, ct);
        var locked = kind == AiProfileKind.Embeddings && await m_index.ActiveFingerprintAsync(ct) is not null;
        return CurrentAiSettings.ToView(kind, assignment, revision, locked);
    }
}
