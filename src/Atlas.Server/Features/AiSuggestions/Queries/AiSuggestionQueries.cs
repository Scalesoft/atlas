using System.Text;
using Atlas.Server.Features.AiSuggestions.Contracts;
using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.AiSuggestions.Queries;

// Reads live in a …Queries namespace, so their transactions commit without waiting for the WAL flush (TransactionIntent).
// Each suggestion runs as: a short read (authorization, the rule-based part, whether the vector part may run) → the model
// call in the endpoint, outside any transaction (WorkingTextEmbedder) → a short read of the vector part, authorized again.
// Messages carrying working text print no text (PrintMembers): Wolverine reports a failed invocation with ToString, and
// AtlasMessageLogging silences that category as well (plan §3, V5).

/// <summary>E2, first read: the actor may edit the page and has assist on (else 404); exact title and alias matches; the
/// phrases left for the vector part.</summary>
public sealed record FindLinkCandidates(Guid PageId, IReadOnlyList<string> Phrases)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("PageId = ").Append(PageId).Append(", Phrases = ").Append(Phrases.Count);
        return true;
    }
}

/// <param name="MeaningPhrases">Multi-word phrases without an exact match that may be compared by meaning.</param>
/// <param name="InAi">The page's space is included in AI (G5); otherwise its text never reaches a model.</param>
public sealed record LinkCandidateLookup(IReadOnlyList<LinkCandidate> Items, IReadOnlyList<string> MeaningPhrases, bool InAi)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Items = ").Append(Items.Count).Append(", MeaningPhrases = ").Append(MeaningPhrases.Count).Append(", InAi = ").Append(InAi);
        return true;
    }
}

/// <summary>E2, second read: the nearest readable title of each embedded phrase (<paramref name="Vectors"/>[i] of
/// <paramref name="Phrases"/>[i]).</summary>
public sealed record FindMeaningLinkCandidates(Guid PageId, IReadOnlyList<string> Phrases, Guid GenerationId, IReadOnlyList<float[]> Vectors)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("PageId = ").Append(PageId).Append(", Phrases = ").Append(Phrases.Count).Append(", GenerationId = ").Append(GenerationId);
        return true;
    }
}

/// <summary>E4, first read: the actor may edit the page and has assist on (else 404); the section text without password
/// references, and whether the page's space is included in AI.</summary>
public sealed record PreparePageOrder(Guid PageId, string Context, IReadOnlyList<Guid> Candidates)
{
    public const int MaxContextLength = 2000;
    public const int MaxCandidates = 20;

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("PageId = ").Append(PageId).Append(", Context = ").Append(Context.Length).Append(" characters, Candidates = ").Append(Candidates.Count);
        return true;
    }
}

public sealed record PageOrderContext(string Text, bool InAi)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Text = ").Append(Text.Length).Append(" characters, InAi = ").Append(InAi);
        return true;
    }
}

/// <summary>E4, second read: the candidates' similarity to the embedded section.</summary>
public sealed record RankPagesByContext(Guid PageId, IReadOnlyList<Guid> Candidates, SemanticQuery Semantic)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("PageId = ").Append(PageId).Append(", Candidates = ").Append(Candidates.Count);
        return true;
    }
}

/// <summary>P1–P3: before a title is embedded, whether the actor may create a page in the space (Writer on the space for
/// its root, EditContent on a page of it below that page, as CreatePage decides) and whether the space is included in AI.</summary>
public sealed record GetCreationScope(Guid SpaceId);

/// <param name="RootAllowed">The actor may create at the root of the space.</param>
public sealed record CreationScope(bool MayCreate, bool RootAllowed, bool InAi)
{
    public static readonly CreationScope None = new(false, false, false);
}

/// <summary>P2: the parent suggested for a page whose title was embedded as <see cref="Semantic"/>; null = nothing to suggest.</summary>
public sealed record SuggestPlacement(Guid SpaceId, SemanticQuery Semantic)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("SpaceId = ").Append(SpaceId);
        return true;
    }
}

/// <summary>P3: a template for a page with this title, by the dictionary and, with <see cref="Semantic"/>, by meaning;
/// null = nothing to suggest.</summary>
public sealed record SuggestTemplate(Guid SpaceId, string Title)
{
    public SemanticQuery? Semantic { get; init; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("SpaceId = ").Append(SpaceId).Append(", Title = ").Append(Title.Length).Append(" characters");
        return true;
    }
}

/// <summary>The answer of <see cref="SuggestPlacement"/>; Wolverine answers a value, so "nothing" is a null inside it.</summary>
public sealed record PlacementLookup(PlacementSuggestion? Suggestion);

/// <summary>The answer of <see cref="SuggestTemplate"/>.</summary>
public sealed record TemplateLookup(TemplateSuggestion? Suggestion);
