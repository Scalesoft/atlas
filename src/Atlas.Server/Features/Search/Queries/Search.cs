using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.Search.Queries;

public sealed record Search(string? Query, Guid? SpaceId, int Offset, int Limit, SearchOptions Options)
{
    public bool Fuzzy { get; init; } = true;
    /// <summary>G2: set only by the omnisearch full pass. A zero-result search is then offered to the miss recorder.</summary>
    public bool RecordMiss { get; init; }
    /// <summary>G2/O3: the space in which the user opened omnisearch; a miss without a space filter belongs to it.</summary>
    public Guid? ContextSpaceId { get; init; }
    /// <summary>S1/S2: the query vector, embedded by the endpoint before the handler (<see cref="ISemanticQuery"/>), never inside
    /// its transaction or lock (plan §1.1). Null: the search is lexical, exactly as without AI.</summary>
    public SemanticQuery? Semantic { get; init; }
    /// <summary>A next page of a result whose <see cref="Contracts.SearchResponse.Relaxed"/> the caller has: true continues the
    /// relaxed result without probing the strict query again, false never relaxes; null decides as for a first page (P2-7).</summary>
    public bool? Relaxed { get; init; }
}
