namespace Atlas.Server.Features.Search.Contracts;

/// <summary>P1: existing pages resembling the title of a page being created. Never blocks creation: a slow
/// lookup answers an empty list, and the web shows nothing on an empty list or an error.</summary>
public sealed record SimilarPagesResponse(IReadOnlyList<SimilarPage> Items);

/// <summary>MatchedBy is "title", "alias" (a previous published title or a published import path) or "heading";
/// HeadingTitle/HeadingId name the section matching the new title, whatever matched the page. Score is the 0-1 similarity
/// SearchQueries.Similar ranks by (at least 0.35; 1 is an identical title). Phase 2 (V5): "meaning" items follow the lexical
/// ones with the createSuggestions capability, published pages only, no section, Score = the cosine similarity of the title
/// or content (at least 0.75).</summary>
public sealed record SimilarPage(Guid Id, Guid SpaceId, string SpaceName, string Title, bool IsDraft,
    string MatchedBy, string? HeadingTitle, string? HeadingId, float Score);
