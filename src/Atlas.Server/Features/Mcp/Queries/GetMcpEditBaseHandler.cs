using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Mcp.Contracts;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>
/// The read before a content write over MCP, in its own transaction; the conversion runs after it, outside any lock, and the save
/// checks the version again (CAS). Through the shared <see cref="PageReader"/>: the working revision only with EditContent (a reader of the page gets 403, anyone
/// else, and a page in a space excluded by G5, the 404 of a missing page), no opening recorded. A copy without password
/// references the actor may not see cannot be written back (403 page_has_hidden_references), and a different version is a
/// version_conflict carrying the current one. The page links of the new Markdown are resolved here, on the actor's rights.
/// </summary>
public sealed class GetMcpEditBaseHandler
{
    private readonly PageReader m_reader;
    private readonly IContentAccess m_access;
    private readonly McpVisibility m_visibility;
    private readonly MarkdownLinkResolver m_links;

    public GetMcpEditBaseHandler(PageReader reader, IContentAccess access, McpVisibility visibility, MarkdownLinkResolver links)
    {
        m_reader = reader;
        m_access = access;
        m_visibility = visibility;
        m_links = links;
    }

    public async Task<McpEditBase> Handle(GetMcpEditBase query, CancellationToken ct)
    {
        PageDto page;
        try
        {
            page = await m_reader.ReadAsync(query.PageId, PageReadVersion.Working, false, ct);
        }
        catch (ApiException missing) when (missing.Status == 404)
        {
            // The working revision of a page the actor may only read is a 404 to the reader; for a write that is the 403 REST gives,
            // never for a page the actor may not see at all (the read below fails with the 404 then) or one switched out of MCP (G5).
            var readable = await m_reader.ReadAsync(query.PageId, PageReadVersion.Default, false, ct);
            await RequireInScopeAsync(readable.SpaceId, ct);
            throw McpWriteErrors.NoWriteRight();
        }
        await RequireInScopeAsync(page.SpaceId, ct);
        // A reader of a Živě page gets the live revision as its working one; writing it is still not allowed.
        if (page.Live)
        {
            throw McpWriteErrors.NoWriteRight();
        }
        if (!page.CanEdit)
        {
            throw McpWriteErrors.HiddenReferences();
        }
        if (!Rules.IsExpectedVersion(page.Version, query.ExpectedVersion))
        {
            throw McpWriteErrors.VersionConflict(page.Version);
        }
        var actor = await m_access.LockActorAsync(page.SpaceId, ct);
        var links = await m_links.ResolveAsync(actor, query.Links, m_visibility.PagesInScopeAsync, ct);
        return new McpEditBase(page.Id, page.SpaceId, page.Version, page.Title, page.ContentJson, links);
    }

    private async Task RequireInScopeAsync(Guid spaceId, CancellationToken ct)
    {
        try
        {
            await m_visibility.RequireSpaceAsync(spaceId, ct);
        }
        catch (ApiException)
        {
            throw new ApiException(404, McpVisibility.MissingPage);
        }
    }
}
