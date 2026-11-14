using System.Text.Json.Serialization;

namespace Atlas.Server.Features.SearchGaps.Contracts;

/// <summary>Why a query is in the gaps (plan phase 3 R9). One person, query and day hold the strongest reason.</summary>
public static class SearchGapReason
{
    /// <summary>The search found nothing.</summary>
    public const string Zero = "zero";

    /// <summary>The answer card said "Bez podkladů" (O16).</summary>
    public const string NoAnswer = "no_answer";

    /// <summary>The person pressed "Nahlásit chybějící obsah".</summary>
    public const string Reported = "reported";

    public static bool IsKnown(string? reason) => reason is Zero or NoAnswer or Reported;

    /// <summary>reported &gt; no_answer &gt; zero.</summary>
    public static int Strength(string reason) => reason switch { Reported => 2, NoAnswer => 1, _ => 0 };
}

/// <summary>Zero-result queries of one space searched by at least <see cref="MinimumUsers"/> different people in the period.</summary>
public sealed record SearchGapList(IReadOnlyList<SearchGapItem> Items, int MinimumUsers, int RetentionDays)
{
    public const int Threshold = 3;
    public const int Retention = 90;
    public const int MaximumItems = 50;
}

/// <summary>Query is the most frequent typed form (lower case, with diacritics); NormalizedQuery identifies the gap for a resolution.
/// Reported and Unanswered count the different people whose strongest reason was "reported" or "no_answer" (never more than
/// Users, so they reveal nothing the gap's k-anonymity does not); both are left out of the JSON when zero.</summary>
public sealed record SearchGapItem(string Query, string NormalizedQuery, int Users, DateOnly LastSearchedOn)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Reported { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Unanswered { get; init; }
}

public sealed record ResolveSearchGapRequest(string? NormalizedQuery);
