using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.PageOrganization.Contracts;

/// <summary>A node of the space tree: a page, or another content type's leaf (<see cref="Type"/>, e.g. <c>board</c>; its
/// Version is 0 and IsPublished true, it has no publication). A <c>reference</c> node names its target (<see cref="TargetId"/>,
/// <see cref="TargetTypeId"/>, <see cref="Anchor"/>); its title is the target's, null with <see cref="Readable"/> false when the
/// account may not read the target.</summary>
public sealed record PageTreeItem(Guid Id, Guid? ParentId, string? Title, int Version, int SortOrder, bool IsPublished, bool CanEdit)
{
    public string Type { get; init; } = "page";
    public PageDecorationDto? Decoration { get; init; }
    public Guid? TargetId { get; init; }
    public string? TargetTypeId { get; init; }
    public string? Anchor { get; init; }
    public bool Readable { get; init; } = true;
    /// <summary>The page's own board (its Purpose board), for an account that may edit the page; the tree's quiet mark opens it.</summary>
    public Guid? AttachedBoardId { get; init; }
    /// <summary>„Řadit abecedně“ of this page: its direct children are shown by title (Domain.TitleOrder).</summary>
    public bool ChildrenByTitle { get; init; }
    /// <summary>5.2: the page's verification state (unverified, verified, expiring, expired) for the link chips that resolve from the
    /// tree; absent while off.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Verification { get; init; }

    /// <summary>Another content type's leaf under <paramref name="parentId"/> (the visible parent, D8).</summary>
    public static PageTreeItem Of(ContentTreeNode node, Guid? parentId) =>
        new(node.Id, parentId, node.Title, 0, node.SortOrder, true, node.CanEdit)
        {
            Type = node.Type, TargetId = node.TargetId, TargetTypeId = node.TargetTypeId, Anchor = node.Anchor, Readable = node.Readable
        };
}
