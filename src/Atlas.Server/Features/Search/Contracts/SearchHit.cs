using System.Text.Json.Serialization;

namespace Atlas.Server.Features.Search.Contracts;

public sealed record SearchHit(Guid Id, Guid SpaceId, string SpaceName, string Title, string Snippet)
{
    public IReadOnlyList<SearchWidget> Widgets { get; init; } = [];
    public bool IsDraft { get; init; }
    public DateTime? ChangedAt { get; init; }
    public DateTime? PublishedAt { get; init; }
    public string? HeadingTitle { get; init; }
    public string? HeadingId { get; init; }
    public string? MatchedAlias { get; init; }

    /// <summary>5.2: the page's verification state (unverified, verified, expiring, expired) for the result's icon; absent while off.
    /// The order of the results does not depend on it (O5).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VerificationState { get; init; }

    /// <summary>S1 "Významová shoda": found by meaning only, the page does not match the query's words. Its snippet, heading
    /// and anchor are the passage's. Written only when true, so a search without AI serializes as before (AI-24).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Semantic { get; init; }

    /// <summary>S2: the section of the published revision closest to the query, with its anchor; a lexical hit gets one only
    /// when the vector confirms a section. Never on a draft; absent without AI.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SearchPassage? Passage { get; init; }

    /// <summary>The whole chunk of <see cref="Passage"/> (raw, up to one chunk), for the MCP search. Never serialized:
    /// the web and the Personal API keep the 320-character passage.</summary>
    [JsonIgnore]
    public string? PassageBody { get; init; }
}

/// <summary>A passage of the knowledge index: plain text of at most 320 characters cut at a word
/// (<c>SearchText.Passage</c>) and the anchor of its section (null for the start of the page).</summary>
public sealed record SearchPassage(string Text, string? HeadingTitle, string? HeadingId);
