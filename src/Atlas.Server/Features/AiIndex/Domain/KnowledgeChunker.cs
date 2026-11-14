using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Atlas.Server.Features.AiIndex.Domain;

/// <summary>A heading of the published revision as <c>SearchSqlText.Headings</c> reports it, in document order: its cleaned
/// title and its persistent anchor (null when the rich document has none).</summary>
public sealed record IndexHeading(string Title, string? HeadingId);

/// <param name="Ordinal">Position in the page, from 0.</param>
/// <param name="HeadingId">Anchor of the section (the anchor of <c>SearchHit.HeadingId</c>); null before the first heading.</param>
/// <param name="Text">What the chunk stores: the context line "{title} › {heading}" (or the title alone), a newline and the body.</param>
/// <param name="EmbeddingText">Exactly what is embedded: the profile's document prefix and <paramref name="Text"/>.</param>
/// <param name="TextHash">SHA-256 of <paramref name="EmbeddingText"/>; an unchanged section keeps its vector.</param>
public sealed record KnowledgeChunkDraft(int Ordinal, string? HeadingId, string? HeadingTitle, string Text, string EmbeddingText, byte[] TextHash);

/// <summary>
/// Splits a published revision into sections by its headings (plan §10, ported from the prototype's <c>Retrieval/Corpus.cs</c>).
/// The input is already safe to embed: the text for AI of the published revision (<c>OutboundDocument.AiText</c>) and the headings
/// of <c>SearchSqlText.Headings</c>, read by SQL in the claim transaction, so passages anchor where search hits do.
/// </summary>
/// <remarks>
/// Heading lines are recognised exactly as <c>SearchSqlText.Headings</c> does (ATX <c>#</c>–<c>######</c> with a blank after
/// the hashes, outside fenced code blocks). A heading line takes the anchor of the next reported heading with its title, so duplicate
/// titles keep their own anchors and the headings of a concealed block, which the text for AI leaves out, are skipped; a line no
/// reported heading names takes the next one. Text before the first heading belongs to the start of the page. Sections are
/// packed paragraph by paragraph into at most <see cref="MaxBodyCharacters"/>; a longer paragraph is split into consecutive
/// pieces (at a sentence boundary when one is in reach, else at a word, else hard) so that all of its text is indexed.
/// Links and images become their text (an image URL is dropped; nothing is downloaded). A page without any text is one
/// chunk of its title. Changing any of this changes <see cref="Version"/>, and the overview then recommends a rebuild.
/// </remarks>
public static partial class KnowledgeChunker
{
    // @2: an over-long paragraph is split across chunks instead of cut (its tail was never indexed). @3: the text for AI, without
    // concealed blocks and with a readable line per contact (SEC-T-1, GAP5-5). IndexViews compares the active generation's
    // ChunkingVersion with this, so existing indexes report RebuildRecommended until they are rebuilt.
    public const string Version = "headings-1800@3";
    public const int MaxBodyCharacters = 1800;
    /// <summary>The context line's share of <c>KnowledgeChunk.Text</c> (at most 2 600 characters).</summary>
    public const int MaxContextCharacters = 790;
    public const int MaxHeadingTitleCharacters = 500;
    private const string Separator = " › ";

    /// <summary>What the title vector embeds: the document prefix and the title.</summary>
    public static string TitleText(string title, string documentPrefix) => documentPrefix + Clip(title.Trim(), MaxContextCharacters);

    public static byte[] Hash(string embeddingText) => SHA256.HashData(Encoding.UTF8.GetBytes(embeddingText));

    public static IReadOnlyList<KnowledgeChunkDraft> Chunk(string title, string markdown, IReadOnlyList<IndexHeading> headings, string documentPrefix)
    {
        title = title.Trim();
        var chunks = new List<KnowledgeChunkDraft>();
        var body = new StringBuilder();
        string? fence = null;
        var headingIndex = -1;
        IndexHeading? current = null;
        foreach (var line in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var marker = FenceMarker().Match(line);
            if (fence is not null)
            {
                if (marker.Success && marker.Groups[1].Value[0] == fence[0] && marker.Groups[1].Value.Length >= fence.Length && FenceClose().IsMatch(line))
                {
                    fence = null;
                }
                body.Append(line).Append('\n');
                continue;
            }
            if (marker.Success)
            {
                fence = marker.Groups[1].Value;
                body.Append(line).Append('\n');
                continue;
            }
            var heading = HeadingLine().Match(line);
            if (!heading.Success)
            {
                body.Append(Summary(line, title)).Append('\n');
                continue;
            }
            Flush(chunks, title, current, body.ToString(), documentPrefix);
            body.Clear();
            var text = CleanHeading(heading.Groups[1].Value);
            var named = headingIndex + 1;
            while (named < headings.Count && headings[named].Title != text)
            {
                named++;
            }
            headingIndex = named < headings.Count ? named : headingIndex + 1;
            current = new IndexHeading(text, headingIndex < headings.Count ? headings[headingIndex].HeadingId : null);
        }
        Flush(chunks, title, current, body.ToString(), documentPrefix);
        if (chunks.Count == 0)
        {
            chunks.Add(Draft(0, null, title, "", documentPrefix));
        }
        return chunks;
    }

    /// <summary>
    /// A Projector summary line names its page ("**Stav nasazení KBN — Production:** …"): the summaries of copied deployments are
    /// otherwise near-equal and the vector of one page's summary answers for another (review K5). The stored projection stays without
    /// the title, so a rename does not rewrite it. No page published before Atlas 3 has the line, so <see cref="Version"/> stays.
    /// </summary>
    private static string Summary(string line, string title)
    {
        const string label = "**" + Platform.Content.RequirementText.SummaryLabel + ":**";
        return title.Length > 0 && line.StartsWith(label, StringComparison.Ordinal)
            ? "**" + Platform.Content.RequirementText.SummaryLabel + " " + title + ":**" + line[label.Length..]
            : line;
    }

    private static void Flush(List<KnowledgeChunkDraft> chunks, string title, IndexHeading? heading, string section, string documentPrefix)
    {
        var part = new StringBuilder();
        foreach (var raw in BlankLine().Split(SimplifyLinks(section)))
        {
            var paragraph = raw.Trim();
            if (paragraph.Length == 0)
            {
                continue;
            }
            foreach (var piece in paragraph.Length > MaxBodyCharacters ? SplitParagraph(paragraph) : [paragraph])
            {
                if (part.Length > 0 && part.Length + 2 + piece.Length > MaxBodyCharacters)
                {
                    chunks.Add(Draft(chunks.Count, heading, title, part.ToString(), documentPrefix));
                    part.Clear();
                }
                if (part.Length > 0)
                {
                    part.Append("\n\n");
                }
                part.Append(piece);
            }
        }
        if (part.Length > 0)
        {
            chunks.Add(Draft(chunks.Count, heading, title, part.ToString(), documentPrefix));
        }
    }

    private static KnowledgeChunkDraft Draft(int ordinal, IndexHeading? heading, string title, string body, string documentPrefix)
    {
        var headingTitle = heading is null ? null : Clip(heading.Title.Trim(), MaxHeadingTitleCharacters);
        var context = Clip(headingTitle is null ? title : title + Separator + headingTitle, MaxContextCharacters);
        var text = body.Length == 0 ? context : context + "\n" + body;
        var embedding = documentPrefix + text;
        return new KnowledgeChunkDraft(ordinal, heading?.HeadingId, headingTitle, text, embedding, Hash(embedding));
    }

    private static string SimplifyLinks(string text) => Link().Replace(text, "$1");

    private static string CleanHeading(string heading)
    {
        var text = TrailingHashes().Replace(heading.Trim(), "");
        return Emphasis().Replace(SimplifyLinks(text), "");
    }

    /// <summary>Consecutive pieces of at most <see cref="MaxBodyCharacters"/>; only the whitespace at a split is dropped.</summary>
    private static List<string> SplitParagraph(string paragraph)
    {
        var pieces = new List<string>();
        var rest = paragraph.AsSpan();
        while (rest.Length > MaxBodyCharacters)
        {
            var window = rest[..(MaxBodyCharacters + 1)];
            var cut = LastSentenceEnd(window);
            if (cut <= MaxBodyCharacters / 2)
            {
                cut = window.LastIndexOfAny(' ', '\n', '\t');
            }
            if (cut <= MaxBodyCharacters / 2)
            {
                cut = MaxBodyCharacters;
            }
            pieces.Add(rest[..cut].TrimEnd().ToString());
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0)
        {
            pieces.Add(rest.ToString());
        }
        return pieces;
    }

    // The index just after the last ". ", "! " or "? " (the whitespace stays out of the piece), or -1.
    private static int LastSentenceEnd(ReadOnlySpan<char> window)
    {
        for (var i = window.Length - 2; i > 0; i--)
        {
            if (char.IsWhiteSpace(window[i + 1]) && window[i] is '.' or '!' or '?')
            {
                return i + 1;
            }
        }
        return -1;
    }

    private static string CutAtWord(string text, int max)
    {
        var cut = text.LastIndexOfAny([' ', '\n', '\t'], max);
        return (cut > max / 2 ? text[..cut] : text[..max]).TrimEnd();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : CutAtWord(text, max);

    // The same patterns as SearchSqlText.Headings (atlas_search_headings until 3.6): fences of three or more backticks or tildes indented by at
    // most three spaces, closed by the same character at least as long with nothing but blanks after it.
    [GeneratedRegex("^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex FenceMarker();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})[ \t\n\r\f\v]*$")]
    private static partial Regex FenceClose();

    [GeneratedRegex(@"^ {0,3}#{1,6}[ \t]+(.+)$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"[ \t]+#+[ \t]*$")]
    private static partial Regex TrailingHashes();

    [GeneratedRegex(@"[*_`]+")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex BlankLine();
}
