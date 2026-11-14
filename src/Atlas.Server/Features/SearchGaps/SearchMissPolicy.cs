using System.Globalization;

namespace Atlas.Server.Features.SearchGaps;

/// <summary>D9: which zero-result queries may be kept at all. A query is kept only when it has at least three normalized
/// characters, at most 120 characters as typed, and no word that looks like a secret pasted by mistake: 20 or more
/// characters, or 12 or more mixing letters and digits. Both the typed words and the normalized words are checked, so
/// punctuation cannot split a token below the limits.</summary>
public static class SearchMissPolicy
{
    public const int MinimumLength = 3;
    public const int MaximumLength = 120;

    public static bool Allows(string? query, string normalized)
    {
        if (normalized.Length < MinimumLength)
        {
            return false;
        }
        var display = Display(query);
        return display.Length <= MaximumLength && !display.Split(' ').Any(LooksSecret) && !normalized.Split(' ').Any(LooksSecret);
    }

    /// <summary>The form shown to the space admin: trimmed, whitespace collapsed, lower case, diacritics kept.</summary>
    public static string Display(string? query)
    {
        var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var display = string.Join(' ', words).ToLower(CultureInfo.InvariantCulture);
        return display.Length > 200 ? display[..200] : display;
    }

    private static bool LooksSecret(string word)
        => word.Length >= 20 || word.Length >= 12 && word.Any(char.IsLetter) && word.Any(char.IsDigit);
}
