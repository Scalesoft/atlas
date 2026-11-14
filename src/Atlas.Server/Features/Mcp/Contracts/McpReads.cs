namespace Atlas.Server.Features.Mcp.Contracts;

/// <summary>A page read for atlas_get_page: the page as the shared reader returned it (password references the actor may not see
/// already removed), where it sits, and the titles of the pages it references that the actor may read in a space MCP may see.</summary>
/// <param name="ProjectedVersion">The version the content belongs to: the published revision's, or the page version for the working one.</param>
public sealed record McpPageRead(PageDto Page, bool Working, int ProjectedVersion, string SpaceName, string Path,
    IReadOnlyDictionary<Guid, string> ReadablePageTitles);

public sealed record McpSpaceItem(Guid SpaceId, string Name, string Description, string Role);

public sealed record McpSpaceList(IReadOnlyList<McpSpaceItem> Items, bool HasMore);

/// <summary>Editor-only facts of a page in atlas_list_pages; never who edits.</summary>
public sealed record McpPageState(Guid PageId, bool HasUnpublishedChanges, bool HasUnsavedLiveEdits);

public sealed record McpPageStates(IReadOnlyList<McpPageState> Items);

/// <summary>G5 for a tool that read through a shared handler: which of the pages exist in a space MCP may see, which spaces it may not.</summary>
public sealed record McpScope(IReadOnlySet<Guid> PagesInScope, IReadOnlySet<Guid> ExcludedSpaces);

public sealed record McpPathRequest(Guid PageId, string SpaceName, string Title);

public sealed record McpPaths(IReadOnlyDictionary<Guid, string> Items);

/// <summary>A page of atlas_get_space_tree: the shared tree item (title, publication, decoration as the actor sees them), its depth
/// and title path within the space, and its tags.</summary>
public sealed record McpTreeEntry(PageOrganization.Contracts.PageTreeItem Item, int Depth, string Path, IReadOnlyList<PageTags.TagDto> Tags);

/// <summary>Total counts every page of the space the actor sees; Items is the requested window in depth-first order.</summary>
public sealed record McpSpaceTree(Guid SpaceId, long Version, int Total, IReadOnlyList<McpTreeEntry> Items)
{
    /// <summary>„Řadit abecedně“ of the space root (PageTreeDto.RootByTitle).</summary>
    public bool RootByTitle { get; init; }
}
