using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.AiSuggestions;

/// <summary>How a phrase of a finished paragraph may be linked (design §7.5 A).</summary>
/// <param name="Normalized">The phrase as titles are compared (<see cref="SearchText"/>), password references removed first.</param>
/// <param name="Exact">May be offered for an exact title or alias match.</param>
/// <param name="Meaning">May also be offered by title similarity (vector): 2–5 words, at least one of them significant.</param>
public sealed record LinkPhrase(string Phrase, string Normalized, bool Exact, bool Meaning);

/// <summary>
/// E2: which phrases deserve a link suggestion. A single word only by an exact match, and only a significant one: at least
/// 4 characters and not a stop word ("návod", "postup", "stránka" … the words every page uses), or an acronym written in
/// capitals ("VPN", "SSO"), the usual title of a short page. A phrase of 2–5 words also by meaning, unless every word is a
/// stop word or shorter than 4 characters: the vector never offers a link for a general word. Rules in code, tests pin them.
/// </summary>
public static class LinkPhrasePolicy
{
    public const int MaxPhrases = 40;
    public const int MaxPhraseLength = 80;
    public const int MaxMeaningWords = 5;

    /// <summary>Normalized (no diacritics, lower case). Tunable with the thresholds of migration 2026100570.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // Words of every page: what the text is about, never which page.
        "navod", "navodu", "navody", "postup", "postupu", "postupy", "stranka", "stranky", "strance", "stranku", "dokument",
        "dokumentu", "dokumenty", "informace", "informaci", "nastaveni", "system", "systemu", "prehled", "poznamka", "poznamky",
        "clanek", "clanku", "kapitola", "kapitole", "sekce", "sekci", "text", "textu", "obrazek", "tabulka", "tabulce", "seznam",
        "seznamu", "priklad", "prikladu", "odkaz", "odkazu", "popis", "popisu", "reseni", "problem", "problemu", "verze", "verzi",
        "soubor", "souboru", "data", "udaje", "pozor", "upozorneni", "dalsi", "nove", "novy", "nova",
        // Czech function words of four letters and more.
        "jako", "nebo", "take", "tedy", "ktery", "ktera", "ktere", "kterou", "jsou", "neni", "bude", "budou", "byla", "bylo",
        "podle", "mezi", "tento", "tato", "toto", "tyto", "jeho", "jeji", "jejich", "kdyz", "proto", "vsak", "pouze", "vice",
        "pred", "pres", "potom", "pak", "zde", "tady", "jinak", "jeste", "uz", "muze", "mohou", "musi", "lze", "nutne", "treba",
        "prosim", "dekuji",
        // English.
        "this", "that", "with", "from", "into", "have", "will", "your", "page", "pages", "guide", "note", "notes", "info",
    };

    /// <summary>The phrase's rules, or null when it can be offered neither way.</summary>
    public static LinkPhrase? Classify(string phrase)
    {
        var text = phrase.Trim();
        var normalized = SearchText.NormalizeContent(SearchText.StripSecretDirectives(text));
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return null;
        }
        if (words.Length == 1)
        {
            var word = words[0];
            var significant = word.Length >= 4 && !StopWords.Contains(word) || IsAcronym(text);
            return significant ? new LinkPhrase(text, normalized, true, false) : null;
        }
        var meaningful = words.Any(word => word.Length >= 4 && !StopWords.Contains(word));
        return meaningful ? new LinkPhrase(text, normalized, true, words.Length <= MaxMeaningWords) : null;
    }

    /// <summary>2–3 letters or digits, written in capitals, with at least one letter: "VPN", "SSO", "CI", "K8S".</summary>
    private static bool IsAcronym(string text)
        => text.Length is >= 2 and <= 3 && text.All(c => char.IsUpper(c) || char.IsDigit(c)) && text.Any(char.IsLetter);
}
