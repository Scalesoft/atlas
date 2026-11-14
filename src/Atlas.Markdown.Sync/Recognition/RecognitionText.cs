using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Atlas.Markdown.Sync.Recognition;

/// <summary>A word of a text: its offsets (UTF-16, as JavaScript counts) and its folded key.</summary>
public readonly record struct RecognitionToken(int Start, int End, string Key);

/// <summary>
/// The one fold and the one tokenizer of the linking engine (audit engine §4.3), the port of the editor's
/// <c>core/recognition/text.ts</c>: the same text gives the same words and keys (tests/fixtures/linking/corpus.json holds them together).
/// </summary>
public static partial class RecognitionText
{
    /// <summary>NFD, no combining marks, invariant lower case (never a locale's): „Jana Nováková“ → „jana novakova“.</summary>
    public static string Fold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                folded.Append(c);
            }
        }
        return folded.ToString().ToLowerInvariant();
    }

    /// <summary>The words of <paramref name="text"/> with their offsets and folded keys.</summary>
    public static IReadOnlyList<RecognitionToken> Tokenize(string text) =>
        Word().Matches(text).Select(match => new RecognitionToken(match.Index, match.Index + match.Length, Fold(match.Value))).ToArray();

    /// <summary>The folded words of a name joined by one space: „Jana  Nováková“ → „jana novakova“.</summary>
    public static string NameKey(string name) => string.Join(' ', Tokenize(name).Select(token => token.Key));

    /// <summary>The stem of a folded word, for Czech inflection: one ending off („jany“ → „jan“, „petrovi“ → „petr“). Only ever an offer.</summary>
    public static string StemOf(string key)
    {
        if (key.Length <= 3)
        {
            return key;
        }
        var stem = Ending().Replace(key, "", 1);
        return stem.Length >= 3 ? stem : key;
    }

    /// <summary>A word: letters and digits, joined by an apostrophe or a hyphen („O’Brien“, „Nováková-Svobodová“, „PX2-1235“).</summary>
    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{M}\p{N}]*(?:['’-][\p{L}\p{M}\p{N}]+)*")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?:ovi|ou|em|[aeiouy])\z")]
    private static partial Regex Ending();
}

/// <summary>
/// The named patterns of the linking engine (<c>core/recognition/patterns.ts</c>): a YouTrack issue key and the standards that only
/// look like one.
/// </summary>
public static partial class IssueKeys
{
    /// <summary>A YouTrack issue key: project, hyphen, number („PX2-1235“). The same source as the editor's.</summary>
    public const string Pattern = "[A-Z][A-Z0-9]+-[1-9][0-9]{0,6}";

    /// <summary>Prefixes of standards and encodings („UTF-8“, „ISO-9001“): never an issue, whatever the projects are.</summary>
    public static readonly IReadOnlySet<string> Deny = new HashSet<string>(StringComparer.Ordinal)
    {
        "AES", "ANSI", "CP", "CVE", "CWE", "CSN", "DIN", "ECMA", "EN", "ES", "GDPR", "HTTP", "IEC", "IEEE", "IPV", "ISO", "JSR", "MD", "PCI",
        "PEP", "RFC", "RSA", "SHA", "SQL", "SSL", "TLS", "UTF", "WIN",
    };

    /// <summary>The issue key <paramref name="body"/> ends with („… viz #PX2-1235“ → <c>#PX2-1235</c>), if its project is one of
    /// <paramref name="projects"/> and no standard's.</summary>
    public static (int From, int To, string Key)? AtEnd(string body, IReadOnlyCollection<string> projects)
    {
        var match = KeyAtEnd().Match(body);
        if (!match.Success)
        {
            return null;
        }
        var key = match.Groups[2].Value;
        var project = key[..key.LastIndexOf('-')];
        if (Deny.Contains(project) || !projects.Contains(project))
        {
            return null;
        }
        return (body.Length - key.Length - match.Groups[1].Length, body.Length, key);
    }

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}#-])(#?)(" + Pattern + @")\z")]
    private static partial Regex KeyAtEnd();
}
