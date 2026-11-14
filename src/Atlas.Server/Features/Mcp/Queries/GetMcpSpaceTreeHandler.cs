using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.PageTags;

namespace Atlas.Server.Features.Mcp.Queries;

/// <summary>The shared tree read of the web (GET /spaces/{id}/tree: locks, the actor's rights per page, hidden parents shown at the
/// root, decorations in one query), then G5 under its locks as atlas_get_labels does, then the tags of the window in one query.</summary>
public sealed class GetMcpSpaceTreeHandler
{
    private readonly GetPageTreeHandler m_tree;
    private readonly McpVisibility m_visibility;
    private readonly PageTagStore m_tags;

    public GetMcpSpaceTreeHandler(GetPageTreeHandler tree, McpVisibility visibility, PageTagStore tags)
    {
        m_tree = tree;
        m_visibility = visibility;
        m_tags = tags;
    }

    public async Task<McpSpaceTree> Handle(GetMcpSpaceTree query, CancellationToken ct)
    {
        var tree = await m_tree.Handle(new GetPageTree(query.SpaceId), ct);
        await m_visibility.RequireSpaceAsync(query.SpaceId, ct);
        // A reference node to a target the account may not read is a lock without a title: nothing for a model.
        var children = tree.Items.Where(item => item.Readable).ToLookup(item => item.ParentId);
        var ordered = new List<(PageTreeItem Item, int Depth, string Path)>(tree.Items.Count);
        // Depth-first in sibling order; the read already put a page with a hidden parent at the root, so every item is reached once.
        var stack = new Stack<(PageTreeItem Item, int Depth, string Path)>(children[null].Reverse().Select(item => (item, 0, item.Title ?? "")));
        while (stack.TryPop(out var entry))
        {
            ordered.Add(entry);
            foreach (var child in children[entry.Item.Id].Reverse())
            {
                stack.Push((child, entry.Depth + 1, entry.Path + " › " + child.Title));
            }
        }
        var window = ordered.Skip(query.Offset).Take(query.Limit).ToArray();
        var tags = await m_tags.ReadManyAsync(window.Select(entry => entry.Item.Id).ToArray(), ct);
        return new McpSpaceTree(tree.SpaceId, tree.Version, ordered.Count,
            window.Select(entry => new McpTreeEntry(entry.Item, entry.Depth, entry.Path, tags.GetValueOrDefault(entry.Item.Id) ?? [])).ToArray())
        {
            RootByTitle = tree.RootByTitle
        };
    }
}
