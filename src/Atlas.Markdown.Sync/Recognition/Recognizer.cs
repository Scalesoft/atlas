using System.Text.RegularExpressions;

namespace Atlas.Markdown.Sync.Recognition;

/// <summary><c>chip</c>: convert at once; <c>unresolved</c>: several people, an unresolved chip waits for the pick; <c>suggest</c>: only
/// offered (aliases, initials, inflected forms, page titles, a truncated lexicon); <c>pending</c>: a key YouTrack confirms first.</summary>
public enum RecognitionDecision
{
    None,
    Chip,
    Unresolved,
    Suggest,
    Pending,
}

/// <summary>A known name (<c>MarkdownRecognitionEntry</c>): <paramref name="Id"/> is the entity's identity within its kind (one candidate
/// however many of its names match); <paramref name="Form"/> <c>full</c> or <c>alias</c> (only ever offered, D4); <paramref name="Auto"/>
/// whether a lone whole match becomes a chip by itself; <paramref name="Cased"/> counts only spelled exactly as written.</summary>
public sealed record RecognitionEntry(string Kind, string Id, string Name, string Form, bool Auto, bool Inflects = false, bool Cased = false);

/// <summary>What a text holds at <paramref name="From"/>..<paramref name="To"/>; <paramref name="Form"/> <c>full</c>, <c>alias</c>,
/// <c>stem</c> or <c>pattern</c>; <paramref name="Candidates"/> one entry per entity.</summary>
public sealed record RecognitionHit(string Kind, int From, int To, string Text, string Form, RecognitionDecision Decision, IReadOnlyList<RecognitionEntry> Candidates);

/// <summary>
/// The pure part of the linking engine (audit engine §4), the port of the editor's <c>core/recognition/recognize.ts</c>: an index of what
/// is known by name (a trie of folded words, read backwards), the issue-key pattern and the decision. No I/O; the golden corpus
/// (tests/fixtures/linking/corpus.json) holds the two ports together.
/// </summary>
public sealed partial class RecognitionIndex
{
    /// <summary>More words than this are no name nor title.</summary>
    private const int MaxWords = 8;

    /// <summary>The most text before the caret a keystroke reads (P17).</summary>
    public const int Window = 200;

    private sealed class Trie
    {
        public Dictionary<string, Trie>? Next;
        public List<RecognitionEntry>? Entries;
    }

    private readonly Trie m_exact = new();
    private readonly Trie m_stems = new();
    private readonly IReadOnlySet<string> m_unresolved;

    /// <summary>The most words of one entry.</summary>
    public int Longest { get; private set; }

    /// <summary>The index of <paramref name="entries"/>; <paramref name="unresolved"/> names the kinds whose several candidates make one
    /// unresolved chip (people); several of any other kind are only offered.</summary>
    public RecognitionIndex(IEnumerable<RecognitionEntry> entries, IReadOnlySet<string>? unresolved = null)
    {
        m_unresolved = unresolved ?? new HashSet<string>();
        foreach (var entry in entries)
        {
            var words = RecognitionText.Tokenize(entry.Name).Select(token => token.Key).ToArray();
            // One word is a name only spelled exactly or as an alias: „Eva“ or „Projekt“ alone is everyday text.
            if (words.Length == 0 || words.Length > MaxWords || (words.Length == 1 && entry.Form == "full" && !entry.Cased))
            {
                continue;
            }
            Longest = Math.Max(Longest, words.Length);
            Add(m_exact, words, entry);
            if (entry.Inflects && entry.Form == "full")
            {
                Add(m_stems, words.Select(RecognitionText.StemOf).ToArray(), entry with { Form = "alias", Auto = false });
            }
        }
    }

    private static void Add(Trie trie, string[] words, RecognitionEntry entry)
    {
        var node = trie;
        // Backwards: a match is read from its last word to the left.
        for (var i = words.Length - 1; i >= 0; i--)
        {
            node.Next ??= new Dictionary<string, Trie>(StringComparer.Ordinal);
            if (!node.Next.TryGetValue(words[i], out var child))
            {
                node.Next[words[i]] = child = new Trie();
            }
            node = child;
        }
        if (node.Entries?.Any(known => known.Kind == entry.Kind && known.Id == entry.Id) != true)
        {
            (node.Entries ??= []).Add(entry);
        }
    }

    private sealed record Found(int Start, List<RecognitionEntry> Entries, bool Stem);

    /// <summary>The longest entry of <paramref name="trie"/> that ends with word <paramref name="end"/>, its words joined by spaces only.</summary>
    private Found? Ending(Trie trie, IReadOnlyList<RecognitionToken> tokens, int end, string text, bool stem)
    {
        Trie? node = trie;
        Found? found = null;
        for (var i = end; i >= 0 && end - i < Longest; i--)
        {
            if (i < end && !Gap().IsMatch(text[tokens[i].End..tokens[i + 1].Start])
                && !(InitialGap().IsMatch(text[tokens[i].End..tokens[i + 1].Start]) && tokens[i].End - tokens[i].Start == 1))
            {
                break;
            }
            if (node.Next is null || !node.Next.TryGetValue(stem ? RecognitionText.StemOf(tokens[i].Key) : tokens[i].Key, out node))
            {
                break;
            }
            var written = text[tokens[i].Start..tokens[end].End];
            var entries = node.Entries?.Where(entry => !entry.Cased || entry.Name == written).ToList();
            if (entries is { Count: > 0 })
            {
                found = new Found(i, entries, stem);
            }
        }
        return found;
    }

    /// <summary>The decision for what matched (§4.5); a truncated lexicon's entries are not <c>Auto</c>, so it never gives a chip.</summary>
    private (RecognitionDecision Decision, string Form, string Kind, RecognitionEntry[] Candidates) Decide(Found found)
    {
        var kind = found.Entries[0].Kind;
        var entries = found.Entries.Where(entry => entry.Kind == kind).ToArray();
        var whole = !found.Stem && entries.All(entry => entry.Form == "full");
        var auto = whole && entries.All(entry => entry.Auto);
        var decision = !auto ? RecognitionDecision.Suggest
            : entries.Length == 1 ? RecognitionDecision.Chip
            : m_unresolved.Contains(kind) ? RecognitionDecision.Unresolved : RecognitionDecision.Suggest;
        return (decision, found.Stem ? "stem" : whole ? "full" : "alias", kind, entries);
    }

    /// <summary>The best match ending with word <paramref name="end"/>: the longer of the exact and the inflected one, the exact one when as long.</summary>
    private RecognitionHit? MatchEnding(IReadOnlyList<RecognitionToken> tokens, int end, string text)
    {
        var exact = Ending(m_exact, tokens, end, text, false);
        var stem = Ending(m_stems, tokens, end, text, true);
        var found = exact is not null && (stem is null || exact.Start <= stem.Start) ? exact : stem;
        if (found is null)
        {
            return null;
        }
        var (from, to) = (tokens[found.Start].Start, tokens[end].End);
        // No piece of an e-mail or a domain („mmu@firma.example.test“, „jana.novakova“).
        var before = from > 0 ? text[from - 1] : '\0';
        var after = to < text.Length ? text[to] : '\0';
        if (before is '@' or '.' || after == '@' || (after == '.' && to + 1 < text.Length && char.IsLetterOrDigit(text[to + 1])))
        {
            return null;
        }
        var (decision, form, kind, candidates) = Decide(found);
        return new RecognitionHit(kind, from, to, text[from..to], form, decision, candidates);
    }

    /// <summary>Every known name in <paramref name="text"/> (a block's prose), left to right, the longest where they overlap, at most
    /// <paramref name="limit"/>.</summary>
    public IReadOnlyList<RecognitionHit> RecognizeText(string text, int limit = int.MaxValue)
    {
        var hits = new List<RecognitionHit>();
        if (Longest == 0)
        {
            return hits;
        }
        var tokens = RecognitionText.Tokenize(text);
        for (var end = 0; end < tokens.Count; end++)
        {
            if (MatchEnding(tokens, end, text) is not { } hit)
            {
                continue;
            }
            // A longer name over the last one replaces it; one that starts inside it is a piece of it.
            while (hits.Count > 0 && hit.From < hits[^1].To)
            {
                if (hit.From > hits[^1].From)
                {
                    break;
                }
                hits.RemoveAt(hits.Count - 1);
            }
            if (hits.Count > 0 && hit.From < hits[^1].To)
            {
                continue;
            }
            hits.Add(hit);
            if (hits.Count >= limit)
            {
                break;
            }
        }
        return hits;
    }

    /// <summary>The window of a keystroke: the text before <paramref name="offset"/>, at most <see cref="Window"/> characters, starting after a space.</summary>
    public static (int Start, string Text) RecognitionWindow(string text, int offset)
    {
        var start = Math.Max(0, offset - Window);
        if (start > 0)
        {
            var space = WhiteSpace().Match(text, start, offset - start);
            start = space.Success ? space.Index : offset;
        }
        return (start, text[start..offset]);
    }

    /// <summary>
    /// What <paramref name="before"/> (its last character just typed) ends with before that character, when it is a space or a
    /// punctuation mark: a known name, else a key of a project of <paramref name="projects"/> to confirm (<c>pending</c>).
    /// </summary>
    public RecognitionHit? RecognizeTail(string before, IReadOnlyCollection<string> projects)
    {
        if (before.Length == 0 || !Ends().IsMatch(before[^1..]))
        {
            return null;
        }
        var body = before[..^1];
        var tokens = RecognitionText.Tokenize(body);
        var last = tokens.Count - 1;
        if (last < 0 || tokens[last].End != body.Length)
        {
            return null;
        }
        var known = Longest > 0 ? MatchEnding(tokens, last, body) : null;
        if (known is not null && known.Decision != RecognitionDecision.Suggest)
        {
            return known;
        }
        return IssueKeys.AtEnd(body, projects) is { } key
            ? new RecognitionHit("issue", key.From, key.To, body[key.From..], "pattern", RecognitionDecision.Pending, [])
            : known;
    }

    /// <summary>
    /// Every hit of a block's prose as a writer reaching each word's end would get it: the names (<see cref="RecognizeText"/>) and the
    /// issue keys of <paramref name="projects"/> that end at a word's end followed by an end character or the end of the block.
    /// </summary>
    public IReadOnlyList<RecognitionHit> RecognizeBlock(string text, IReadOnlyCollection<string> projects, int limit = int.MaxValue)
    {
        var hits = RecognizeText(text, limit).ToList();
        if (projects.Count == 0)
        {
            return hits;
        }
        foreach (var token in RecognitionText.Tokenize(text))
        {
            if (hits.Count >= limit)
            {
                break;
            }
            if ((token.End == text.Length || Ends().IsMatch(text[token.End..(token.End + 1)])) && IssueKeys.AtEnd(text[..token.End], projects) is { } key
                && !hits.Any(hit => key.From < hit.To && hit.From < key.To))
            {
                hits.Add(new RecognitionHit("issue", key.From, key.To, text[key.From..key.To], "pattern", RecognitionDecision.Pending, []));
            }
        }
        return hits.OrderBy(hit => hit.From).ToArray();
    }

    /// <summary>What ends a typed word: a space or a punctuation mark.</summary>
    [GeneratedRegex(@"^[\s.,;:!?)\]}“”""'»…–—]$")]
    private static partial Regex Ends();

    /// <summary>Only spaces between the words of a name.</summary>
    [GeneratedRegex("^ +$")]
    private static partial Regex Gap();

    /// <summary>After an initial also its dot and a space („J. Bureš“), as recognize.ts.</summary>
    [GeneratedRegex(@"^\. +$")]
    private static partial Regex InitialGap();

    [GeneratedRegex(@"\s")]
    private static partial Regex WhiteSpace();
}
