using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Domain;
using Atlas.Server.Features.PageLinks;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>
/// The page through the shared <see cref="PageReader"/> (content rights, the private draft rule, password references the actor may
/// not see removed, the opening recorded with the channel mcp), then G5 in the same transaction: an excluded space answers 404 and
/// the rollback takes the recorded opening with it. A reader refusal (not_published) is preceded by G5 too, so it never tells an
/// excluded page from a missing one. The Markdown projection runs in the tool, after this transaction.
/// </summary>
public sealed class GetMcpPageHandler
{
    private readonly ISession m_session;
    private readonly PageReader m_reader;
    private readonly IContentAccess m_access;
    private readonly McpVisibility m_visibility;
    private readonly McpPagePaths m_paths;
    private readonly PageLinkStore m_links;
    private readonly PageVerifications.PageVerificationReader m_verifications;

    public GetMcpPageHandler(ISession session, PageReader reader, IContentAccess access, McpVisibility visibility, McpPagePaths paths,
        PageLinkStore links, PageVerifications.PageVerificationReader verifications)
    {
        m_session = session;
        m_reader = reader;
        m_access = access;
        m_visibility = visibility;
        m_paths = paths;
        m_links = links;
        m_verifications = verifications;
    }

    public async Task<McpPageRead> Handle(GetMcpPage query, CancellationToken ct)
    {
        PageDto page;
        try
        {
            page = await m_reader.ReadAsync(query.PageId, query.Working ? PageReadVersion.Working : PageReadVersion.PublishedOrLive, query.RecordOpening, ct);
        }
        catch (ApiException error) when (error.Code == "not_published")
        {
            // The reader's own refusal must not tell an excluded page from an unknown id, so G5 is decided before it is returned. Only this refusal path
            // pays the extra read; a successful read keeps the shared lock order and is checked below.
            await m_visibility.RequirePageAsync(query.PageId, ct);
            throw;
        }
        try
        {
            await m_visibility.RequireSpaceAsync(page.SpaceId, ct);
        }
        catch (ApiException)
        {
            throw new ApiException(404, McpVisibility.MissingPage);
        }
        var actor = await m_access.LockActorAsync(page.SpaceId, ct);
        var space = await m_session.GetAsync<Space>(page.SpaceId, ct) ?? throw new ApiException(404, McpVisibility.MissingPage);
        var path = (await m_paths.DescribeAsync(actor, new Dictionary<Guid, (string, string)> { [page.Id] = (space.Name, page.Title) }, ct))[page.Id];
        // The chips whose titles the projection shows; at most 100, as the editor's batch resolution.
        var referenced = PageLinkTargets.Of(page.ContentJson, hrefs: false).Take(100).ToArray();
        var titles = new Dictionary<Guid, string>();
        if (referenced.Length > 0)
        {
            var readable = await m_links.ResolveAsync(actor, referenced, ct);
            var inScope = await m_visibility.PagesInScopeAsync(readable.Select(x => x.Id).ToArray(), ct);
            foreach (var reference in readable.Where(x => inScope.Contains(x.Id)))
            {
                titles[reference.Id] = reference.Title;
            }
        }
        var version = query.Working || page.Live ? page.Version : page.PublishedVersion ?? page.Version;
        // 5.2: the verification block (plan §7); the tool says when the source expired.
        page = page with { Verification = await m_verifications.ReadAsync(page.Id, ct) };
        return new McpPageRead(page, query.Working, version, space.Name, path, titles);
    }
}
