using NHibernate;
using Atlas.Server.Features.PageLinks;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Domain;

/// <summary>
/// "Prostor › Rodič › … › Stránka" for pages the caller has already decided the actor may read. An ancestor the actor may not list
/// is "…" (a run of them is one "…", so not even the depth shows); readable ancestors carry the title the actor would open
/// (PageLinkStore.ResolveAsync: the working title for an editor, the published one otherwise).
/// </summary>
public sealed class McpPagePaths
{
    public const string Separator = " › ";
    public const string Hidden = "…";
    private const int MaxDepth = 64;

    private readonly ISession m_session;
    private readonly PageLinkStore m_links;

    public McpPagePaths(ISession session, PageLinkStore links)
    {
        m_session = session;
        m_links = links;
    }

    /// <param name="pages">Page id → (space name, the page's own title as the actor sees it).</param>
    public async Task<IReadOnlyDictionary<Guid, string>> DescribeAsync(UserAccount actor,
        IReadOnlyDictionary<Guid, (string SpaceName, string Title)> pages, CancellationToken ct)
    {
        if (pages.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }
        var rows = await m_session.CreateSQLQuery($"""
            WITH RECURSIVE chain AS (
              SELECT p."Id" AS page, p."ParentId" AS ancestor, 1 AS depth FROM "Page" p WHERE p."Id" IN (:ids)
              UNION ALL
              SELECT c.page, p."ParentId", c.depth + 1 FROM chain c JOIN "Page" p ON p."Id" = c.ancestor
              WHERE c.depth < {MaxDepth}
            )
            SELECT page, ancestor, depth FROM chain WHERE ancestor IS NOT NULL
            """)
            .AddScalar("page", NHibernateUtil.Guid)
            .AddScalar("ancestor", NHibernateUtil.Guid)
            .AddScalar("depth", NHibernateUtil.Int32)
            .SetParameterList("ids", pages.Keys.ToArray())
            .ListAsync<object[]>(ct);
        var chains = rows.GroupBy(row => (Guid)row[0])
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => (int)row[2]).Select(row => (Guid)row[1]).ToArray());
        var ancestors = chains.Values.SelectMany(chain => chain).Distinct().ToArray();
        var titles = ancestors.Length == 0
            ? new Dictionary<Guid, string>()
            : (await m_links.ResolveAsync(actor, ancestors, ct)).ToDictionary(x => x.Id, x => x.Title);
        var result = new Dictionary<Guid, string>();
        foreach (var (pageId, (spaceName, title)) in pages)
        {
            var parts = new List<string> { spaceName };
            foreach (var ancestor in chains.GetValueOrDefault(pageId) ?? [])
            {
                var part = titles.GetValueOrDefault(ancestor) ?? Hidden;
                if (part != Hidden || parts[^1] != Hidden)
                {
                    parts.Add(part);
                }
            }
            parts.Add(title);
            result[pageId] = string.Join(Separator, parts);
        }
        return result;
    }
}
