using System.Text.Json.Serialization;

namespace Atlas.Server.Features.Search.Contracts;

public sealed record SearchResponse(IReadOnlyList<SearchHit> Items, bool HasMore)
{
    public IReadOnlyList<SearchSpaceHit> Spaces { get; init; } = [];

    /// <summary>Other content types' hits (boards, interaction spec §6), best first, on the first page of a full pass: their own
    /// ranking, beside the pages' (Items), as Spaces are. Written only when there is one, so a wiki without them answers as before.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SearchResourceHit>? Resources { get; init; }
    public int? NextOffset { get; init; }

    /// <summary>The vector part of the hybrid search ran (the source chip "Významové hledání"). Written only when true.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Semantic { get; init; }

    /// <summary>S3 "Měli jste na mysli": a readable published title or alias close to a weak query (no model, works without
    /// AI, O5). Only on the first page of a full pass with fewer than three results; written only when there is one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DidYouMean { get; init; }

    /// <summary>K9, S2-3: nothing had all the query's words, so these pages have at least one of its significant words ("nenalezeno pro
    /// všechna slova"). A next page passes it back (<c>relaxed=true</c>). Written only when true.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Relaxed { get; init; }
}
