namespace Atlas.Server.Platform.Content;

/// <summary>
/// What the merge asks the caller, who knows the actor: <see cref="ResolveTitle"/> finds a <c>[[Název]]</c> among the pages
/// the actor may read (and, for MCP, outside spaces excluded from AI); <see cref="PageExists"/> says whether a linked page id
/// is such a page; <see cref="NewHeadingId"/> gives the id of a new heading (a UUID in production), also the id of a cell of a table a new chart links to. The merge never tells
/// a missing page from an unreadable one.
/// </summary>
public sealed record MergeContext(Func<string, PageLinkResolution> ResolveTitle, Func<Guid, bool> PageExists, Func<string> NewHeadingId);
