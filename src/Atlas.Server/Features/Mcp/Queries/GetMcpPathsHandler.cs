using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Domain;

namespace Atlas.Server.Features.Mcp.Queries;

public sealed class GetMcpPathsHandler
{
    private readonly IContentAccess m_access;
    private readonly McpPagePaths m_paths;

    public GetMcpPathsHandler(IContentAccess access, McpPagePaths paths)
    {
        m_access = access;
        m_paths = paths;
    }

    public async Task<McpPaths> Handle(GetMcpPaths query, CancellationToken ct)
    {
        var actor = await m_access.LockActorAsync(null, ct);
        var pages = query.Pages.DistinctBy(x => x.PageId).ToDictionary(x => x.PageId, x => (x.SpaceName, x.Title));
        return new McpPaths(await m_paths.DescribeAsync(actor, pages, ct));
    }
}
