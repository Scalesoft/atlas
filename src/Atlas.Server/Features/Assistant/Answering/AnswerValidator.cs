using System.Text.RegularExpressions;

namespace Atlas.Server.Features.Assistant.Answering;

/// <summary>An answer after validation: only sources that were passed, plain-text sentences, counted corrections.</summary>
public sealed record ValidatedAnswer(
    bool Answerable,
    IReadOnlyList<AnswerSentence> Sentences,
    IReadOnlyList<int> Cited,
    int InvalidCitations,
    int DroppedSentences)
{
    /// <summary>True when the flow ends as "no sources": the model abstained or nothing survived validation.</summary>
    public bool NoSources => !Answerable;
}

/// <summary>
/// Pure validation of a model answer against the sources that were actually passed (plan §7, R8). The UI draws citations
/// only from the validated <c>Sources</c>, never from text. No database, no model, no logging.
/// </summary>
public static partial class AnswerValidator
{
    public const int MaxSentences = 12;
    public const int MaxSentenceLength = 600;

    /// <param name="sourceCount">Number of sources passed to the prompt; valid citation numbers are 1..sourceCount.</param>
    public static ValidatedAnswer Validate(AnswerResult? result, int sourceCount)
    {
        if (result is null || !result.Answerable || result.Sentences is null)
        {
            return NoSources(0, 0);
        }

        var invalid = 0;
        var dropped = 0;
        var kept = new List<AnswerSentence>();
        foreach (var sentence in result.Sentences)
        {
            var numbers = sentence.Sources ?? [];
            var valid = numbers.Where(number => number >= 1 && number <= sourceCount).Distinct().Order().ToArray();
            invalid += numbers.Count(number => number < 1 || number > sourceCount);
            var text = Clean(sentence.Text);
            if (text.Length == 0)
            {
                continue;
            }
            if (valid.Length == 0)
            {
                dropped++;
                continue;
            }
            if (kept.Count < MaxSentences)
            {
                kept.Add(new AnswerSentence(text, valid));
            }
        }

        if (kept.Count == 0)
        {
            return NoSources(invalid, dropped);
        }
        return new ValidatedAnswer(true, kept, kept.SelectMany(sentence => sentence.Sources).Distinct().Order().ToArray(), invalid, dropped);
    }

    /// <summary>R8: removes citation markers, HTML tags, Markdown images and links (keeps link text), trims to 600 characters.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        var cleaned = MarkdownImage().Replace(text, "");
        cleaned = MarkdownLink().Replace(cleaned, "$1");
        cleaned = HtmlTag().Replace(cleaned, "");
        cleaned = CitationMarker().Replace(cleaned, "");
        cleaned = Whitespace().Replace(cleaned, " ").Trim();
        return cleaned.Length <= MaxSentenceLength ? cleaned : cleaned[..MaxSentenceLength].TrimEnd();
    }

    /// <summary>The source numbers a text writes itself ("[2]", "(zdroj 1, 3)"): what <see cref="Clean"/> removes.</summary>
    public static IEnumerable<int> WrittenCitations(string text)
        => CitationMarker().Matches(text).SelectMany(match => Digits().Matches(match.Value)).Select(number => int.TryParse(number.Value, out var value) ? value : 0);

    /// <summary>The grounding of plain text (agent answers, plain-string sentences): the text names the source's title.</summary>
    public static bool NamesTitle(string text, string title)
        => title.Trim() is { Length: >= MinTitleLength } name && text.Contains(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>A shorter title would match words of any answer.</summary>
    private const int MinTitleLength = 3;

    private static ValidatedAnswer NoSources(int invalid, int dropped) => new(false, [], [], invalid, dropped);

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"</?[A-Za-z][^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s*(?:\[\s*(?:zdroj\s*)?\d+(?:\s*[,;]\s*\d+)*\s*\]|\(\s*(?:zdroj\s*)?\d+(?:\s*[,;]\s*\d+)*\s*\))", RegexOptions.IgnoreCase)]
    private static partial Regex CitationMarker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
