using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Atlas.Server.Features.Search.Domain;

public static partial class SearchText
{
    public static string Normalize(string? input)
    {
        if (input?.Length > 200)
        {
            throw new ArgumentException("Hledaný text smí mít nejvýše 200 znaků.");
        }
        return NormalizeContent(input ?? "");
    }

    /// <summary>The words of a typed lookup as SQL <c>lower(public.unaccent(…))</c> compares them: lower case, no diacritics, the
    /// punctuation kept („jana@“). A directory lookup asks for every word anywhere (contacts, the editor's people).</summary>
    public static string[] LookupWords(string? input) => (input ?? "").Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Aggregate(new StringBuilder(), (text, c) => text.Append(char.ToLowerInvariant(c)))
        .ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(8).ToArray();

    internal static string NormalizeContent(string input)
    {
        var accentsRemoved = new StringBuilder();
        foreach (var rune in (input ?? "").Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.NonSpacingMark)
            {
                accentsRemoved.Append(Rune.ToLowerInvariant(rune));
            }
        }
        // Keep these aliases in sync with SearchSqlText.Normalize.
        // Canonical alphanumeric lexemes preserve C# / C++ before punctuation removal.
        var technical = CPlusPlus().Replace(accentsRemoved.ToString(), "cplusplus");
        technical = CSharpSymbol().Replace(technical, "csharp");
        technical = CppAlias().Replace(technical, "cplusplus");
        technical = CSharpWords().Replace(technical, "csharp");
        var result = new StringBuilder();
        foreach (var rune in technical.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (Rune.IsLetterOrDigit(rune))
            {
                result.Append(Rune.ToLowerInvariant(rune));
            }
            else if (result.Length > 0 && result[^1] != ' ')
            {
                result.Append(' ');
            }
        }
        return result.ToString().Trim();
    }

    public static bool IsSearchable(string normalized) => normalized.Length >= 2;
    /// <summary>P1 pre-selection: any significant word (3+ characters, at most 16) of a normalized title, OR'ed for
    /// to_tsquery. Normalized tokens are letters and digits only. Empty when the title has no significant word.</summary>
    public static string AnyWordQuery(string normalized) => string.Join(" | ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token.Length >= 3).Distinct(StringComparer.Ordinal).Take(16));
    public static string PrefixQuery(string normalized) => string.Join(" & ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(16).Select(token => token + ":*"));

    public static (string Expression, bool HasPhrase) QueryExpression(string? input)
    {
        _ = Normalize(input); // Apply the same bounded-input rule before tokenizing.
        var expressions = new List<string>();
        var hasPhrase = false;
        foreach (Match match in QueryParts().Matches(input ?? ""))
        {
            var quoted = match.Groups[1].Success;
            var tokens = Normalize(quoted ? match.Groups[1].Value : match.Groups[2].Value)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }
            hasPhrase |= quoted;
            expressions.Add(quoted ? "(" + string.Join(" <-> ", tokens) + ")" : string.Join(" & ", tokens.Select(WordTerm)));
        }
        return (string.Join(" & ", expressions), hasPhrase);
    }

    /// <summary>
    /// The fallback of a query that matched nothing (K9): a whole question ("Na co čekáme v KBN?") has words no page has, verb forms
    /// the stemmer does not know included, so its significant words (3+ characters, not a question or function word, at most 16) are
    /// OR'ed, each as in <see cref="QueryExpression"/>. Null for a phrase, a query without such words, or when it would not differ.
    /// </summary>
    public static string? AnyWordExpression(string? input)
    {
        var (strict, phrase) = QueryExpression(input);
        var relaxed = string.Join(" | ", SignificantWords(Normalize(input)).Select(WordTerm));
        return phrase || relaxed.Length == 0 || relaxed == strict ? null : relaxed;
    }

    /// <summary>The words a question is searched by: of a normalized text, each word of 3+ characters that is no question or function
    /// word, once, at most 16 (search's fallback and the answer's sources).</summary>
    public static IReadOnlyList<string> SignificantWords(string normalized) => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.Ordinal).Where(token => token.Length >= 3 && !IsFunctionWord(token)).Take(16).ToArray();

    /// <summary>A word matches its prefix or its stem (indexed beside the exact words, migration 2026102030).</summary>
    public static string WordTerm(string token) => Stem(token) is var stem && stem != token ? $"({token}:* | {stem})" : token + ":*";

    /// <summary>The most common Czech (and English) question and function words, normalized (no diacritics).</summary>
    public static bool IsFunctionWord(string normalized) => FunctionWords.Contains(normalized);

    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "jak", "jaky", "jaka", "jake", "jakou", "jakym", "jakem", "kde", "kdo", "kdy", "kdyz", "kam", "odkud", "proc", "cim", "koho",
        "komu", "ktery", "ktera", "ktere", "kterou", "kterym", "kolik", "muzu", "mohu", "muze", "muzeme", "lze", "mam", "mame", "mate",
        "musim", "musi", "chci", "potrebuji", "pro", "pri", "pod", "nad", "pred", "mezi", "bez", "nebo", "ale", "ani", "jsou", "jsem",
        "jsme", "jste", "byt", "bude", "budu", "neni", "nejsou", "tak", "take", "tento", "tato", "toto", "tyto", "ten", "tam", "tady",
        "jen", "jeste", "aby", "abych", "jako", "nas", "vas", "nam", "vam", "nami", "vami", "mne", "mnou", "tebe", "tobe", "tebou",
        "jim", "jimi", "nim", "nimi", "nich", "jemu", "nemu", "jeho", "jeji", "jejich", "svuj", "sve", "the", "and", "how",
        "what", "where", "when", "who", "why", "for", "with", "can", "does",
        "co", "je", "to", "se", "si", "na", "do", "po", "za", "ze", "od", "ve", "ke", "ku", "ty", "ta", "by", "mi", "mu", "ji", "a", "i",
        "o", "u", "k", "s", "v", "z", "is", "of", "in", "on", "an",
    };

    /// <summary>Light Czech stemmer of a normalized word, the twin of SearchSqlText.Stem: tiskarna, tiskarnu,
    /// tiskarny and tiskarne share "tiskarn". Words of four letters or fewer stay.</summary>
    // shortcut: case/number endings only (no verbs, no consonant alternations like ruka/ruce), upgrade with a real stemmer if needed.
    public static string Stem(string word)
    {
        foreach (var ending in StemEndings)
        {
            if (word.Length - ending.Length >= 4 && word.EndsWith(ending, StringComparison.Ordinal))
            {
                return word[..^ending.Length];
            }
        }
        return word;
    }

    private static readonly string[] StemEndings = ["ami", "ach", "ich", "ymi", "ech", "um", "ou", "em", "im", "am", "a", "u", "y", "e", "i", "o"];

    [GeneratedRegex(@"""([^""]*)""|([^\s""]+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex QueryParts();

    // Plain text only: callers render snippets as text, never through v-html. Password references (editor 0.6.0 directives)
    // never reach a snippet, whoever searches; ts_headline may cut one at the fragment's edge, so a partial one there goes too.
    public static string Snippet(string markdown, string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The summary's line repeats the titles of the blocks it counts; the hit is in the block (U3-2).
        markdown = SummaryLine().Replace(markdown, "");
        // Review U2-12: a hit in a requirement block shows that block's line alone, though the page's summary or notes name it earlier.
        // The text is the search's headline fragment, so its first line may be a block line cut before its marker.
        var lines = markdown.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if ((RequirementLine().IsMatch(lines[i]) || (i == 0 && RequirementTitleEnd().IsMatch(lines[i])))
                && SnippetText(lines[i]) is var line && tokens.Any(token => Hit(line, token) >= 0))
            {
                var before = lines.Take(i).Any(x => x.Trim().Length > 0);
                var after = lines.Skip(i + 1).Any(x => x.Trim().Length > 0) || line.Length > 220;
                return (before ? "…" : "") + line[..Math.Min(220, line.Length)] + (after ? "…" : "");
            }
        }
        var plain = SnippetText(markdown);
        var hit = tokens.Select(token => Hit(plain, token)).Where(index => index >= 0).DefaultIfEmpty(-1).Min();
        var start = Math.Max(0, hit - 60);
        var length = Math.Min(220, plain.Length - start);
        return (start > 0 ? "…" : "") + plain.Substring(start, length) + (start + length < plain.Length ? "…" : "");
    }

    /// <summary>
    /// Review U3-2: whether a headline fragment shows the hit only in a requirement summary's line, or not at all beside one. ts_headline
    /// cannot see an accented word and then returns the page's start, on a requirement page its summary, whose derived line names the
    /// blocks it counts; such a hit's snippet is cut from the page's text instead (SearchHandler).
    /// </summary>
    public static bool NeedsPageText(string fragment, string query)
    {
        if (!SummaryLine().IsMatch(fragment))
        {
            return false;
        }
        var text = SnippetText(SummaryLine().Replace(fragment, " "));
        return !query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(token => Hit(text, token) >= 0);
    }

    private static int Hit(string text, string token) => CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, token, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);

    /// <summary>The plain text a snippet is cut from: requirement lines as title and state, then the readable text of
    /// <see cref="Excerpt"/> (UX SE3-6). The headline fragment may start or end inside a link, so a cut link keeps only its text.</summary>
    private static string SnippetText(string markdown)
    {
        var text = LinkTargetStart().Replace(LinkCutStart().Replace(markdown, "$1"), "");
        return PlainText(LinkCutEnd().Replace(RequirementLinesAsPlainText(text), "$1"));
    }

    /// <summary>S2: the text of a knowledge-index passage as plain text of at most <see cref="PassageLength"/> characters, cut at
    /// a word with an ellipsis. Like <see cref="Snippet"/>: password references never pass (the index strips them already;
    /// this is the second line), tags go, and Markdown markers that would show as characters (heading hashes, emphasis,
    /// code fences, quotes, list bullets; an underscore stays, identifiers need it) are dropped.</summary>
    public static string Passage(string text) => Passage(RequirementLinesAsPlainText(text), PassageLength);

    /// <summary>
    /// Review U16: a Projector requirement line ("- [x] **SSL certifikát** — hotovo · vstup · klíč: portal.ssl") as a person reads it in a
    /// hit, its title and state: no task marker, bold or key. The agent's longer passage keeps the key, which the projector tools take.
    /// </summary>
    private static string RequirementLinesAsPlainText(string markdown)
        => RequirementKey().Replace(RequirementTitleEnd().Replace(TaskMarker().Replace(markdown, "$1"), " — "), "");

    /// <summary><see cref="Passage(string)"/> of at most <paramref name="length"/> characters: the longer text the agent's
    /// <c>atlas_search</c> gives the model.</summary>
    public static string Passage(string text, int length)
    {
        var plain = SecretDirective().Replace(text, " ");
        plain = PartialSecretDirectiveEnd().Replace(PartialSecretDirectiveStart().Replace(plain, " "), " ");
        plain = MarkdownMarkers().Replace(Tags().Replace(plain, " "), " ");
        plain = Whitespace().Replace(plain, " ").Trim();
        if (plain.Length <= length)
        {
            return plain;
        }
        var cut = plain.LastIndexOf(' ', length - 1);
        return plain[..(cut > length / 2 ? cut : length - 1)].TrimEnd() + "…";
    }

    public const int PassageLength = 320;

    /// <summary>
    /// The readable text of Markdown at most <paramref name="length"/> characters long, cut at a word with an ellipsis (a board's
    /// page card): what <see cref="Passage(string)"/> drops, and also table pipes and separator rows, the syntax of links and
    /// images (their text stays), underscore emphasis and backslash escapes (the escaped character stays).
    /// </summary>
    public static string Excerpt(string markdown, int length)
    {
        var plain = PlainText(markdown);
        if (plain.Length <= length)
        {
            return plain;
        }
        var cut = plain.LastIndexOf(' ', length - 1);
        return plain[..(cut > length / 2 ? cut : length - 1)].TrimEnd() + "…";
    }

    /// <summary>The readable text of Markdown on one line: <see cref="Excerpt"/> before its cut, also what a snippet is cut from.</summary>
    private static string PlainText(string markdown)
    {
        // A component of the dialect (```a-contact …) reads as its values: no fence, key or id; a fragment cut inside one as well.
        markdown = IndentedDialectKey().Replace(DialectFence().Replace(markdown, fence =>
            DialectItem().Replace(DialectKey().Replace(fence.Groups["body"].Value, DialectValue), "")), DialectValue);
        // An escaped character survives the marker rules below as a private-use stand-in.
        var plain = Escape().Replace(markdown, match => ((char)(0xE000 + match.Groups[1].Value[0])).ToString());
        // A backslash at the end of a line is a hard break, not text.
        plain = HardBreak().Replace(plain, " ");
        plain = SecretDirective().Replace(plain, " ");
        plain = PartialSecretDirectiveEnd().Replace(PartialSecretDirectiveStart().Replace(plain, " "), " ");
        plain = HeadingAttributes().Replace(PartialHeadingAttributesStart().Replace(plain, " "), "");
        plain = LinkOrImage().Replace(TableSeparator().Replace(plain, " "), "$1");
        // Markers go without a trace, so "**word**," stays "word,".
        plain = MarkdownMarkers().Replace(Tags().Replace(plain, " "), "");
        plain = UnderscoreEmphasis().Replace(plain, "$1").Replace('|', ' ');
        plain = new string(plain.Select(c => c is >= '\uE000' and <= '\uE0FF' ? (char)(c - 0xE000) : c).ToArray());
        return Whitespace().Replace(plain, " ").Trim();
    }

    /// <summary>Password references out of an author's working text before it may reach a model (E2, E4): the exact rule of
    /// <c>SearchSqlText.StripSecrets</c>, which the index applies to published content.</summary>
    public static string StripSecretDirectives(string text) => SecretDirective().Replace(text, " ");

    // Linear by construction, so no match timeout: a wall-clock limit also trips on a fast match the thread was paused in (a GC, a busy
    // host) and failed searches and indexing. NonBacktracking guarantees linear time; the engine has no lookarounds, so the patterns that
    // need them are fixed text between one-character guards (and the underscore pair, which scans to the next „_“ only).
    // SearchTextTests.Every_text_pattern_costs_linear_time keeps them linear.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])c\+\+(?!\+)", RegexOptions.CultureInvariant)] private static partial Regex CPlusPlus();
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])c#(?!#)", RegexOptions.CultureInvariant)] private static partial Regex CSharpSymbol();
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])cpp(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant)] private static partial Regex CppAlias();
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])c\s+sharp(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant)] private static partial Regex CSharpWords();

    // No „<“ inside a tag: `[^>]*` scanned from every unclosed „<“ to the end, quadratic, and timed out on long pages (UX ED-12).
    [GeneratedRegex("<[^<>]*>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex Tags();
    [GeneratedRegex(@"::?secret\{[^{}\n]*\}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex SecretDirective();
    [GeneratedRegex(@"::?secret\{[^{}\n]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex PartialSecretDirectiveEnd();
    // A leading attribute tail: one word, or anything naming a reference attribute, up to the closing brace.
    [GeneratedRegex(@"^\s*(?:[^\s{}]*|[^{}\n]*\b(?:secretId|field|fields|showCopy|showDetails|autoHideSeconds)=[^{}\n]*)\}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PartialSecretDirectiveStart();
    // A requirement heading's attribute block ("### Název {#id state=done kind=inbound}", RequirementHeadingMarkdown), whole or cut at
    // the fragment's end, and its tail at the fragment's start: anything naming a requirement attribute up to the closing brace.
    [GeneratedRegex(@"(?m)[ \t]*\{#[^{}\n]*(?:\}|$)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex HeadingAttributes();
    [GeneratedRegex(@"^\s*[^{}\n]*\b(?:state|kind|party|partyContact|system|from|stateLabel)=[^{}\n]*\}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PartialHeadingAttributesStart();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex Whitespace();
    [GeneratedRegex(@"(?m)^([ \t>]*)[-*+] \[[ xX-]\] (?:\*\*)?", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex TaskMarker();
    [GeneratedRegex(@"\*\* — ", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex RequirementTitleEnd();
    [GeneratedRegex(@"^[ \t>]*[-*+] \[[ xX-]\] \*\*.*\*\* — ", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex RequirementLine();
    [GeneratedRegex(@" · klíč: [a-z0-9][a-z0-9.-]*", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex RequirementKey();
    // The summary as RequirementText writes it ("**Stav nasazení:** …", RequirementText.SummaryLabel), whole lines.
    private static string DialectValue(Match key) => HiddenDialectKey().IsMatch(key.Groups["key"].Value) ? "" : key.Groups["value"].Value;
    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,})a-[\w-]*[^\n]*(?:\n(?<body>.*?))?(?:\n {0,3}\k<fence>[ \t]*(?=\n|\z)|\z)",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DialectFence();
    [GeneratedRegex(@"^[ \t]*(?:-[ \t]+)?(?<key>[A-Za-z][\w-]*)[ \t]*:[ \t]*(?<value>.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DialectKey();
    [GeneratedRegex(@"^[ \t]*-[ \t]+", RegexOptions.Multiline | RegexOptions.CultureInvariant)] private static partial Regex DialectItem();
    [GeneratedRegex(@"^[ \t]{2,}(?:-[ \t]+)?(?<key>[A-Za-z][\w-]*)[ \t]*:[ \t]*(?<value>.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IndentedDialectKey();
    // Ids and technical keys of a component, which no reader needs in a snippet.
    [GeneratedRegex(@"^(?:\w*Ids?|id|version|kind|photo|internalUser)$", RegexOptions.CultureInvariant)] private static partial Regex HiddenDialectKey();
    [GeneratedRegex(@"(?m)^\*\*Stav nasazení[^\n*]*:\*\*[^\n]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex SummaryLine();
    [GeneratedRegex(@"(?m)^\s{0,3}(?:#{1,6}\s|>\s?|[-*+]\s|\d+[.)]\s)|`{3,}|~{3,}|[*`]{1,3}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex MarkdownMarkers();
    [GeneratedRegex(@"\\(?=\r?\n|$)", RegexOptions.CultureInvariant)] private static partial Regex HardBreak();
    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|>~])", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex Escape();
    [GeneratedRegex(@"(?m)^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)*\|?\s*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex TableSeparator();
    [GeneratedRegex(@"!?\[([^\]\n]*)\]\([^)\s]*(?:\s+""[^""\n]*"")?\)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex LinkOrImage();
    // A fragment that starts inside a link: its text then "](target)", or the rest of its target up to ")" (a path or URL only,
    // so a word in parentheses stays). One that ends inside a link: "[text](target…" or "[text…".
    [GeneratedRegex(@"^([^\[\]\n]*)\]\([^)\s]*(?:\s+""[^""\n]*"")?\)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex LinkCutStart();
    [GeneratedRegex(@"^[^\s()\[\]]*[/#?=][^\s()\[\]]*\)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex LinkTargetStart();
    [GeneratedRegex(@"!?\[([^\]\n]*)(?:\]\([^)\s]*)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)] private static partial Regex LinkCutEnd();
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])_([^_\s](?:[^_\n]*[^_\s])?)_(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant)] private static partial Regex UnderscoreEmphasis();
}
