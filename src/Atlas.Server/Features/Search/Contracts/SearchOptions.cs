namespace Atlas.Server.Features.Search.Contracts;

/// <summary>Content is "all" (published pages and the drafts the actor may read), "published" or "drafts".
/// "all" is the default: publication state is a filter people opt into, never the thing they search by.
/// AiScopeOnly (MCP, D10): leave out pages of spaces switched out of AI and MCP (AccessSql.SpaceInAi); paging stays the
/// unfiltered offsets, so a page of results may be shorter than the limit while more follow.</summary>
public sealed record SearchOptions(string Content = "all", string ChangedWithin = "any", string Sort = "relevance", bool AiScopeOnly = false);
