using Atlas.Server.Features.Mcp.Contracts;

namespace Atlas.Server.Features.Mcp.Queries;

public sealed class CheckMcpScopeHandler
{
    private readonly IContentAccess m_access;
    private readonly McpVisibility m_visibility;

    public CheckMcpScopeHandler(IContentAccess access, McpVisibility visibility)
    {
        m_access = access;
        m_visibility = visibility;
    }

    public async Task<McpScope> Handle(CheckMcpScope query, CancellationToken ct)
    {
        // The account first, as every content read: a revoked credential answers 401 here too.
        await m_access.LockActorAsync(null, ct);
        return new McpScope(await m_visibility.PagesInScopeAsync(query.PageIds, ct), await m_visibility.ExcludedSpacesAsync(query.SpaceIds, ct));
    }
}
