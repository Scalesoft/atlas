using System.Text;

namespace Atlas.Server.Features.AiSuggestions.Contracts;

// Request bodies carry the author's working text: their ToString never prints it, so no runtime log can (plan §3, V5).

/// <summary>E2 <c>POST /api/assist/link-candidates</c>: phrases (n-grams of 1–5 words) of a finished paragraph of the page being
/// edited; at most 40, each at most 80 characters.</summary>
public sealed record LinkCandidatesRequest(Guid PageId, IReadOnlyList<string>? Phrases)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("PageId = ").Append(PageId).Append(", Phrases = ").Append(Phrases?.Count ?? 0);
        return true;
    }
}

/// <param name="Phrase">The phrase as the client sent it, so it can find its words in the paragraph.</param>
/// <param name="MatchedBy"><c>title</c> (exact normalized title), <c>alias</c> (a previous title or import path) or
/// <c>meaning</c> (title similarity of a multi-word phrase, with the <c>editorLinks</c> capability).</param>
public sealed record LinkCandidate(string Phrase, Guid PageId, Guid SpaceId, string SpaceName, string Title, string MatchedBy);

public sealed record LinkCandidatesResponse(IReadOnlyList<LinkCandidate> Items);

/// <summary>E4 <c>POST /api/assist/page-order</c>: the text of the section around the cursor (at most 2 000 characters, without
/// blocks with a detected password; the server removes password references too) and the <c>[[</c> candidates (at most 20).</summary>
public sealed record PageOrderRequest(Guid PageId, string? Context, IReadOnlyList<Guid>? Candidates)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("PageId = ").Append(PageId).Append(", Context = ").Append(Context?.Length ?? 0).Append(" characters, Candidates = ")
            .Append(Candidates?.Count ?? 0);
        return true;
    }
}

/// <summary>Similarity 0–1 of the candidate's content to the section; only readable published candidates, most similar
/// first. A candidate that is missing (a draft, a hidden page) keeps its lexical place.</summary>
public sealed record PageOrderItem(Guid PageId, float Similarity);

public sealed record PageOrderResponse(IReadOnlyList<PageOrderItem> Items);

public sealed record PlacementPathItem(Guid Id, string Title);

/// <summary>P2 <c>GET /api/pages/placement-suggestion</c>: where the new page belongs. <see cref="ParentId"/> null is the root
/// of the space (then <see cref="ParentTitle"/> is null and <see cref="Path"/> empty); otherwise <see cref="Path"/> runs from
/// the top of the space down to the parent itself.</summary>
public sealed record PlacementSuggestion(Guid? ParentId, string? ParentTitle, IReadOnlyList<PlacementPathItem> Path);

/// <summary>P3 <c>GET /api/pages/template-suggestion</c>. MatchedBy is <c>dictionary</c> (always, without AI too) or
/// <c>meaning</c> (title similarity, with the <c>createSuggestions</c> capability).</summary>
public sealed record TemplateSuggestion(Guid TemplateId, string Title, string MatchedBy);
