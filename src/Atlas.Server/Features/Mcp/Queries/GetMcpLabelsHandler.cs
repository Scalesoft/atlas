using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.PageTags.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Queries;

public sealed class GetMcpLabelsHandler
{
    private readonly ISession m_session;
    private readonly McpVisibility m_visibility;
    private readonly PageDecorationHandler m_decorations;
    private readonly PageTagsHandler m_tags;

    public GetMcpLabelsHandler(ISession session, McpVisibility visibility, PageDecorationHandler decorations, PageTagsHandler tags)
    {
        m_session = session;
        m_visibility = visibility;
        m_decorations = decorations;
        m_tags = tags;
    }

    public async Task<McpLabels> Handle(GetMcpLabels query, CancellationToken ct)
    {
        // The shared reads take the page's locks (content shared, space FOR SHARE) and decide the actor's right to read it; a page they
        // may not read is the same 404 as a missing one. G5 is decided after them, under those locks, as atlas_get_page does, so a
        // switch of the space out of MCP cannot interleave with the read.
        var decoration = await m_decorations.Handle(new GetPageDecoration(query.PageId), ct);
        var page = (await m_session.GetAsync<Page>(query.PageId, ct))!;
        try
        {
            await m_visibility.RequireSpaceAsync(page.SpaceId, ct);
        }
        catch (ApiException)
        {
            throw new ApiException(404, McpVisibility.MissingPage);
        }
        var tags = await m_tags.Handle(new GetPageTags(query.PageId), ct);
        return new McpLabels(page.Id, page.SpaceId, decoration.Decoration, tags.Tags);
    }
}
