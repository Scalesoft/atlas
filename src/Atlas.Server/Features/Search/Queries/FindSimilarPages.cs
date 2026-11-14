using System.Text;
using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.Search.Queries;

/// <summary>SpaceId only ranks pages of the creation's space first; every readable space is searched.
/// ExcludePageId leaves out the page being checked (phase 2: "possible duplicate" before publication).</summary>
public sealed record FindSimilarPages(string? Title, Guid? SpaceId, Guid? ExcludePageId, int Limit = FindSimilarPages.DefaultLimit)
{
    public const int DefaultLimit = 3;

    /// <summary>P1 vector part (V5): the title embedded by the endpoint before the handler, with the <c>createSuggestions</c>
    /// capability. Null: today's lexical suggestions only, byte for byte.</summary>
    public SemanticQuery? Semantic { get; init; }

    // The title being typed is the author's working text: never printed (plan §3, V5).
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("SpaceId = ").Append(SpaceId).Append(", ExcludePageId = ").Append(ExcludePageId).Append(", Limit = ").Append(Limit)
            .Append(", Semantic = ").Append(Semantic is not null);
        return true;
    }
}
