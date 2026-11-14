using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Pages.Commands;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>
/// A space excluded by G5 answers exactly as one the actor may not reach (the 404 of the shared space check), so the answer never
/// tells an excluded space from a hidden one. Then the create's own refusals and the template as the actor reads it
/// (<see cref="CreatePageHandler.PrepareAsync"/>), which the create decides again under its locks.
/// </summary>
public sealed class PrepareMcpDraftHandler
{
    private readonly IContentAccess m_access;
    private readonly McpVisibility m_visibility;
    private readonly CreatePageHandler m_create;
    private readonly MarkdownLinkResolver m_links;

    public PrepareMcpDraftHandler(IContentAccess access, McpVisibility visibility, CreatePageHandler create, MarkdownLinkResolver links)
    {
        m_access = access;
        m_visibility = visibility;
        m_create = create;
        m_links = links;
    }

    public async Task<PageMarkdownCreateBase> Handle(PrepareMcpDraft query, CancellationToken ct)
    {
        var spaceId = query.Request.SpaceId;
        var actor = await m_access.LockActorAsync(spaceId, ct);
        try
        {
            await m_visibility.RequireSpaceAsync(spaceId, ct);
        }
        catch (ApiException)
        {
            throw McpWriteErrors.MissingSpace();
        }
        var template = await m_create.PrepareAsync(query.Request, ct);
        return new PageMarkdownCreateBase(template, await m_links.ResolveAsync(actor, query.Links, m_visibility.PagesInScopeAsync, ct));
    }
}
