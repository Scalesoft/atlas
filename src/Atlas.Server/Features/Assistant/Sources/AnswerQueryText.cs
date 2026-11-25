using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.Assistant.Sources;

/// <summary>
/// The lexical arguments of <c>atlas_answer_sources</c> for a question (plan phase 3 R3). Search ANDs every word of a query; a
/// question is a sentence ("Jak se připojím k VPN z domova?") whose words rarely all appear in one page, so the answer ORs its
/// significant words (prefix or stem like search's fallback, at least 3 characters, without the most common Czech question and
/// function words; at most 16) and ranks by the lexical score of search. A word of three characters matches only itself: OR'ed, its
/// prefix hits unrelated titles ("nám" was nam:*, naming) and a title hit ranks above meaning. The prototype's lexical baseline was OR'ed too
/// (the retrieval prototype). <see cref="Query"/> is the normalized question cut to 200
/// characters at a word, for the title tiers of search.
/// </summary>
public sealed record AnswerQueryText(string Query, string Prefix, string Fts)
{
    public const int MaxQueryCharacters = 200;
    public const int MaxTerms = 16;

    /// <summary>
    /// A search query without question and function words, for the agent's <c>atlas_search</c>: the model often passes the whole
    /// question ("Co je to freebusy"), and search ANDs every word, so a page had to contain "co", "je" and "to" as well. Numbers
    /// and short terms ("API 10.3", "AI") stay. A quoted phrase, or a query that is only such words, is returned unchanged.
    /// </summary>
    public static string Keywords(string query)
    {
        if (query.Contains('"', StringComparison.Ordinal))
        {
            return query;
        }
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = words.Where(word => !SearchText.IsFunctionWord(SearchText.NormalizeContent(word))).ToArray();
        return kept.Length == 0 || kept.Length == words.Length ? query : string.Join(' ', kept);
    }

    /// <summary>Null when the question has no word to search by: the page adapter then retrieves nothing.</summary>
    public static AnswerQueryText? Create(string? question)
    {
        var normalized = Cut(SearchText.NormalizeContent(question ?? ""), MaxQueryCharacters);
        if (!SearchText.IsSearchable(normalized))
        {
            return null;
        }
        var terms = SearchText.SignificantWords(normalized);
        if (terms.Count == 0)
        {
            // Only short or common words: search by them rather than by nothing.
            terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Where(word => word.Length >= 2).Take(MaxTerms).ToArray();
        }
        return terms.Count == 0 ? null : new AnswerQueryText(normalized, normalized + "%", string.Join(" | ", terms.Select(term => term.Length > 3 ? SearchText.WordTerm(term) : term)));
    }

    /// <summary>The first <paramref name="max"/> characters, cut at a word when one ends in the second half.</summary>
    internal static string Cut(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max)
        {
            return text;
        }
        var cut = text.LastIndexOf(' ', max);
        return (cut > max / 2 ? text[..cut] : text[..max]).TrimEnd();
    }
}
