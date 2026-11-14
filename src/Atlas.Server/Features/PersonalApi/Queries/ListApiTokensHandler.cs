using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Queries;

public sealed class ListApiTokensHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly McpModuleState m_mcp;
    private readonly McpAddress m_address;

    public ListApiTokensHandler(ISession session, IContentAccess access, McpModuleState mcp, McpAddress address)
    {
        m_session = session;
        m_access = access;
        m_mcp = mcp;
        m_address = address;
    }

    public async Task<ApiTokenProfile> Handle(ListApiTokens query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(null, ct);
        var current = await CurrentApiPermission.LoadAsync(m_session, actor.Id, ct);
        var permission = CurrentApiPermission.ToDto(current);
        // A user has at most 20 live credentials. Historical metadata is bounded separately.
        var tokens = await m_session.QueryOver<PersonalApiToken>()
            .Where(x => x.UserId == actor.Id)
            .OrderBy(x => x.ActiveFrom).Desc
            .Take(100)
            .ListAsync(ct);
        await ApiTokenCollections.LoadAsync(m_session, tokens, ct);
        var now = DateTime.UtcNow;
        var mcpEnabled = await m_mcp.IsEnabledAsync(ct);
        return new ApiTokenProfile(permission.CanIssueTokens, permission.Version,
            tokens.Select(token => ApiTokenView.ToDto(token, permission.Level, actor, now, mcpEnabled)).ToArray())
        {
            Level = permission.Level,
            McpEnabled = mcpEnabled,
            McpUrl = mcpEnabled ? m_address.EndpointUrl() : null
        };
    }
}
