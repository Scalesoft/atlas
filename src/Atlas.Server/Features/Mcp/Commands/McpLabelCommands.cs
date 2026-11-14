using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.PageTags;

namespace Atlas.Server.Features.Mcp.Commands;

/// <summary>atlas_set_decoration: the shared decoration write (icon and colour, or both null to clear) after G5.</summary>
public sealed record SetMcpDecoration(Guid PageId, SetPageDecorationRequest Request);

/// <summary>atlas_set_tags with a full set: the shared tag write.</summary>
public sealed record SetMcpTags(Guid PageId, SetPageTagsRequest Request);

/// <summary>atlas_set_tags with add and remove: the shared change that keeps the other tags.</summary>
public sealed record ChangeMcpTags(Guid PageId, ChangePageTagsRequest Request);

/// <summary>One change of atlas_apply_labels: the decoration write (null = unchanged) and/or the tag change (null = unchanged).</summary>
public sealed record McpLabelChange(Guid PageId, SetPageDecorationRequest? Decoration, ChangePageTagsRequest? Tags);

/// <summary>atlas_apply_labels: up to 100 changes of distinct pages in one transaction, all or nothing.</summary>
public sealed record ApplyMcpLabels(IReadOnlyList<McpLabelChange> Changes);

/// <summary>The state of one changed page after the batch, in the order of the changes; null where that part was not changed.</summary>
public sealed record McpAppliedLabels(Guid PageId, PageDecorationState? Decoration, PageTagState? Tags);
