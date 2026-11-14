using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.PageLinks;

/// <summary>
/// Resolves the page links of Markdown written over the API or MCP (K2 <see cref="MergeContext"/>) with the actor's rights: a page
/// counts only when the actor may list it (the editor's link rule, <see cref="PageLinkStore"/>), over MCP also only when its space
/// takes part in AI and MCP (G5: <paramref name="inScope"/> of <c>ResolveAsync</c>, MCP's own filter). A title matching one such page
/// is unique, several are ambiguous; none, missing, unreadable and excluded all answer the same "none".
/// </summary>
public sealed class MarkdownLinkResolver
{
    private const int MatchesPerTitle = 2;

    private readonly PageLinkStore m_links;

    public MarkdownLinkResolver(PageLinkStore links)
    {
        m_links = links;
    }

    /// <param name="inScope">MCP's G5 filter of page ids (<c>McpVisibility.PagesInScopeAsync</c>); null for the personal API.</param>
    public async Task<MarkdownLinkTargets> ResolveAsync(UserAccount actor, MarkdownLinkRequest request,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlySet<Guid>>>? inScope, CancellationToken ct)
    {
        if (request.Titles.Count == 0 && request.PageIds.Count == 0)
        {
            return MarkdownLinkTargets.None;
        }
        var pages = new HashSet<Guid>();
        if (request.PageIds.Count > 0)
        {
            var listed = (await m_links.ResolveAsync(actor, request.PageIds.Distinct().ToArray(), ct)).Select(x => x.Id).ToArray();
            pages.UnionWith(inScope is null ? listed : (IEnumerable<Guid>)await inScope(listed, ct));
        }
        var titles = new Dictionary<string, PageLinkResolution>(StringComparer.Ordinal);
        if (request.Titles.Count > 0)
        {
            var found = await m_links.FindByTitleAsync(actor, request.Titles, MatchesPerTitle, inScope is not null, ct);
            foreach (var group in found.GroupBy(x => x.Asked, StringComparer.Ordinal))
            {
                var matches = group.ToArray();
                titles[group.Key] = matches.Length == 1 ? PageLinkResolution.Unique(matches[0].Id, matches[0].Title) : PageLinkResolution.Ambiguous;
            }
        }
        return new MarkdownLinkTargets(titles, pages);
    }
}
