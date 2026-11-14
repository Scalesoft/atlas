using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Collaboration.Domain;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Atlas.Markdown.Sync;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// Reads the editor's Markdown dialect, as <see cref="EditorMarkdownWriter"/> writes it, back into rich nodes (strict mode
/// for MCP): paragraphs, headings (levels 1–6, as in the editor), quotes, the five GitHub alerts as callouts,
/// lists, task lists, GFM tables, code blocks, rules, and the marks bold, italic, strike, <c>==highlight==</c>, code and
/// link. Component placeholders <c>⟦c:N⟧</c> become <see cref="SlotNodeType"/> nodes, page links by title <c>[[Název]]</c>
/// <see cref="PageLinkNodeType"/> nodes (both resolved by <see cref="RichMarkdownMerge"/>), <c>\⟦</c> and <c>\⟧</c> literal
/// brackets. An attachment of the page is the one thing Markdown may embed: <c>![popis](/api/attachments/{id}/content)</c> standing
/// in a paragraph is the editor's <c>image</c> block (<see cref="ImageNode"/>; the text around it stays paragraphs) and
/// <c>[text](/api/attachments/{id}/download)</c> a link; whether the attachment is the page's own, active and an image is the
/// save's check. Nothing else gains meaning: raw HTML is text, any other image a link (or text), a link outside <c>http</c>,
/// <c>https</c>, <c>mailto</c>, Atlas pages (<c>#/pages/{id}</c>, <c>atlas://pages/{id}</c>) and attachment downloads is text,
/// each with a warning. With the <c>math</c> and <c>diagrams</c> plugins (<see cref="PagePlugins"/>) formulas and diagrams are read as
/// the editor reads its dialect (<c>markdownInput.ts</c>, <c>markdownImportSyntax.ts</c>): <c>$…$</c> and <c>$$…$$</c> in running text
/// as <c>mathInline</c>, <c>$$</c> lines and <c>```math</c> as <c>mathBlock</c>, <c>```mermaid</c> as <c>diagram</c>; data the editor's
/// validator refuses stays code or text, as there. With the <c>contacts</c> and <c>charts</c> plugins the <c>a-</c> components of the
/// dialect (<see cref="MarkdownDialect"/>): <c>```a-contact</c> as <c>contactCard</c>, <c>[Jméno](a-contact:?name=…)</c> as
/// <c>contactInline</c>, <c>```a-chart</c> as <c>chart</c> with the table its <c>{#id}</c> paragraph names; any other <c>a-</c> name is
/// reserved (an issue, kept as code or text; <c>```a-code &lt;jazyk&gt;</c> is code), and in strict mode every issue is
/// 400 <c>invalid_document</c>. Without plugins, and for any other wiki syntax, it stays code and text: no other component comes from
/// Markdown. Pure and deterministic, but for the new ids of a linked table's cells (<c>newId</c>).
/// </summary>
public static partial class MarkdownToRich
{
    /// <summary>The node a component placeholder parses to (<c>attrs.index</c>); never stored, the merge replaces it.</summary>
    public const string SlotNodeType = "$slot";

    /// <summary>
    /// The node <c>[[Název]]</c> parses to (<c>attrs.title</c>): a page link by title that the merge resolves against the
    /// pages the actor may read, or keeps as text. Never stored. The writer escapes every bracket in text, so a projection
    /// never contains one.
    /// </summary>
    public const string PageLinkNodeType = "$pageLink";

    private const int MaxPageLinkTitle = 200;

    public const char PlaceholderOpen = '⟦';
    public const char PlaceholderClose = '⟧';

    private const int MaxDepth = 32;

    // Parses that read the lines of an unclosed $$ again as text; beyond them such lines stay literal (crafted input is quadratic).
    private const int MaxMathPasses = 8;

    /// <summary>The content plugins of every page's editor (the host schema): the dialect the API and MCP read.</summary>
    public static FrozenSet<string> PagePlugins { get; } = CollaborationSchema.CurrentPluginVersions.Keys.ToFrozenSet(StringComparer.Ordinal);

    // Every plugin of the pinned editor at its version: a parsed component is validated as the content of a document.
    private static readonly string ValidatorPlugins = JsonSerializer.Serialize(HostDocumentValidator.Instance.Schema.Plugins.Values.Select(x => new { id = x.Id, version = x.Version }));

    private static readonly MarkdownPipeline Pipeline = CreatePipeline();

    private static readonly Dictionary<string, string> CalloutVariants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NOTE"] = "note", ["TIP"] = "tip", ["WARNING"] = "warning", ["IMPORTANT"] = "important", ["CAUTION"] = "caution",
    };

    [GeneratedRegex(@"^\[!(?<kind>[A-Za-z]+)\][ \t]*$")]
    private static partial Regex AlertMarker();

    [GeneratedRegex(@"^(?:atlas://pages/|/?#/pages/)(?<id>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})(?<heading>\?heading=[A-Za-z0-9][A-Za-z0-9._-]{0,127})?$")]
    private static partial Regex PageLink();

    // A history line „- 30. 10. 2026 — text“ (the MCP help's format): CommonMark reads "30." and "10." as two nested ordered lists.
    [GeneratedRegex(@"^([ \t]*(?:[-*+][ \t]+)?)(\d{1,2})\.([ \t]+\d{1,2}\.[ \t]+\d{4})", RegexOptions.Multiline)]
    private static partial Regex LeadingDate();

    [GeneratedRegex(@"^[ \t]*(`{3,}|~{3,})")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^<br\s*/?>$", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    /// <summary>A GFM delimiter row.</summary>
    [GeneratedRegex(@"^[ \t]*\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$")]
    private static partial Regex DelimiterRow();

    [GeneratedRegex(@"\s*\r?\n\s*")]
    private static partial Regex LineBreakRun();

    [GeneratedRegex(@"^/api/attachments/(?<id>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})/(?<kind>content|download)$")]
    private static partial Regex AttachmentUrl();

    public static string Placeholder(int index)
    {
        return PlaceholderOpen + "c:" + index.ToString(CultureInfo.InvariantCulture) + PlaceholderClose;
    }

    /// <param name="plugins">The page's content plugins (<see cref="PagePlugins"/>); without them no dialect component is read.</param>
    /// <param name="strict">
    /// A write through the API or MCP (spec §2.6, O11): an unknown or invalid <c>a-</c> component is 400 <c>invalid_document</c> naming
    /// every one with its line and column. Otherwise (older Markdown, the projection's own round trip) it stays a code block or literal
    /// text with a warning.
    /// </param>
    /// <param name="newId">The id of a cell of a table a chart links to (default <c>chart-</c> and a new UUID, as the editor).</param>
    public static MarkdownToRichResult Parse(string markdown, IReadOnlySet<string>? plugins = null, bool strict = false, Func<string>? newId = null)
    {
        var conversion = Convert(markdown, plugins, newId ?? (() => "chart-" + Guid.NewGuid().ToString("D")), 0, out var blocks);
        if (strict && conversion.Errors.Count > 0)
        {
            throw InvalidComponents(conversion.Errors);
        }
        return new MarkdownToRichResult(blocks, conversion.Warnings);
    }

    /// <summary>The blocks of <paramref name="markdown"/> read at <paramref name="depth"/> (a cell of a ```a-table is read one level down).</summary>
    private static Conversion Convert(string markdown, IReadOnlySet<string>? plugins, Func<string> newId, int depth, out JsonArray blocks)
    {
        // markdown-it reads every line ending as \n; Markdig would keep CR in code, formulas and diagrams.
        markdown = EscapeLeadingDates(markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        var dialect = new Dialect(plugins?.Contains("math") == true, plugins?.Contains("diagrams") == true)
        {
            Components = plugins is not null, Contacts = plugins?.Contains("contacts") == true, Charts = plugins?.Contains("charts") == true,
            Overviews = plugins?.Contains("atlas-requirements") == true, Requirements = plugins?.Contains("atlas-requirements") == true,
        };
        var headings = new Dictionary<int, JsonObject>();
        var invalid = new List<(int Line, int Column, string Message)>();
        if (dialect.Requirements)
        {
            markdown = ReadHeadingAttributes(markdown, headings, invalid);
        }
        var conversion = new Conversion(markdown, dialect, newId, plugins) { HeadingAttributes = headings };
        foreach (var (line, column, message) in invalid)
        {
            conversion.Issue(true, line, column, "{…}", "Neplatné atributy nadpisu: " + message);
        }
        MarkdownDocument document;
        try
        {
            document = ParseDocument(markdown, dialect);
        }
        catch (ArgumentException)
        {
            // Markdig's own nesting limit ("too deeply nested"): the client's input, as the converter's own depth limit below.
            throw new ApiException(400, "Markdown je příliš hluboce vnořený.", "invalid_markdown");
        }
        conversion.LinkChartTables(document);
        blocks = conversion.Blocks(document, depth);
        conversion.ReadCharts();
        return conversion;
    }

    /// <summary>A Czech date starting a line or a list item outside fenced code with its day escaped (<c>30\. 10. 2026</c>), as the
    /// editor's writer stores it, so the line stays text and not two nested ordered lists.</summary>
    private static string EscapeLeadingDates(string markdown)
    {
        if (!LeadingDate().IsMatch(markdown))
        {
            return markdown;
        }
        var lines = markdown.Split('\n');
        string? fence = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (Fence().Match(lines[i]) is { Success: true } open && (fence is null || open.Groups[1].Value.StartsWith(fence, StringComparison.Ordinal)))
            {
                fence = fence is null ? open.Groups[1].Value : null;
            }
            else if (fence is null)
            {
                lines[i] = LeadingDate().Replace(lines[i], @"$1$2\.$3");
            }
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The trailing attribute block of a heading line at column 0 outside fenced code (<see cref="RequirementHeadingMarkdown"/>): its
    /// attributes by the line's index, the block itself blanked to spaces (offsets stay, the heading's text ends before it). An invalid
    /// block stays text and is an issue.
    /// </summary>
    private static string ReadHeadingAttributes(string markdown, Dictionary<int, JsonObject> headings, List<(int, int, string)> invalid)
    {
        if (!markdown.Contains('{', StringComparison.Ordinal))
        {
            return markdown;
        }
        var lines = markdown.Split('\n');
        string? fence = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (Fence().Match(lines[i]) is { Success: true } open && (fence is null || open.Groups[1].Value.StartsWith(fence, StringComparison.Ordinal)))
            {
                fence = fence is null ? open.Groups[1].Value : null;
                continue;
            }
            if (fence is not null || !lines[i].StartsWith('#') || RequirementHeadingMarkdown.BlockStart(lines[i], out var body) is not (>= 0 and var start))
            {
                continue;
            }
            var attributes = RequirementHeadingMarkdown.Read(body, out var error);
            if (error is not null)
            {
                invalid.Add((i + 1, start + 1, error));
                continue;
            }
            headings[i] = attributes;
            lines[i] = lines[i][..start] + new string(' ', lines[i].Length - start);
        }
        return headings.Count == 0 ? markdown : string.Join('\n', lines);
    }

    /// <summary>400 <c>invalid_document</c> naming every invalid component in order of position, the first one's position as well.</summary>
    private static ApiException InvalidComponents(List<DialectIssue> errors)
    {
        var ordered = errors.OrderBy(x => x.Line).ThenBy(x => x.Column).ToList();
        return new ApiException(400,
            "Markdown obsahuje neplatné komponenty: " + string.Join("; ", ordered.Take(20).Select(x => x.Describe()))
            + (ordered.Count > 20 ? " a další" : "") + ". Opravte je (blok kódu s jazykem a-… se píše ```a-code a-…); nic se neuložilo.",
            "invalid_document",
            new Dictionary<string, object?>
            {
                ["line"] = ordered[0].Line,
                ["column"] = ordered[0].Column,
                ["errors"] = ordered.Select(x => new Dictionary<string, object?>
                {
                    ["line"] = x.Line, ["column"] = x.Column, ["construct"] = x.Construct, ["message"] = x.Message,
                }).ToList(),
            });
    }

    /// <summary>An unknown or invalid dialect component, or a warning about one, at its 1-based line and column (the first backtick or <c>[</c>).</summary>
    private sealed record DialectIssue(int Line, int Column, string Construct, string Message)
    {
        public string Describe() => $"řádek {Line.ToString(CultureInfo.InvariantCulture)}, sloupec {Column.ToString(CultureInfo.InvariantCulture)}: {Message}";
    }

    /// <summary>
    /// The link target the parser keeps: an absolute <c>http</c>, <c>https</c> or <c>mailto</c> URL without credentials, an
    /// Atlas page (<c>atlas://pages/{id}</c> is stored as <c>#/pages/{id}</c>, <c>?heading=</c> kept) or an attachment download
    /// (<c>/api/attachments/{id}/download</c>, stored canonical); null for anything else.
    /// </summary>
    public static string? SafeHref(string url)
    {
        if (PageLink().Match(url) is { Success: true } page)
        {
            return url.StartsWith("atlas://", StringComparison.Ordinal) ? "#/pages/" + page.Groups["id"].Value + page.Groups["heading"].Value : url;
        }
        if (AttachmentUrl().Match(url) is { Success: true } attachment)
        {
            return attachment.Groups["kind"].Value == "download" ? CanonicalAttachment(attachment, "download") : null;
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "mailto" || uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0) ? url : null;
    }

    /// <summary>The canonical <c>/api/attachments/{id}/content</c> of an image the parser embeds; null for any other address.</summary>
    public static string? AttachmentImageSrc(string url)
    {
        return AttachmentUrl().Match(url) is { Success: true } match && match.Groups["kind"].Value == "content" ? CanonicalAttachment(match, "content") : null;
    }

    /// <summary>
    /// The editor's image node as its upload writes it (tiptap Image with RichImage's presentation, attributes in the editor's
    /// order), with what Markdown carries: the address, the alternative text and an optional title. Size and presentation are the
    /// editor's defaults; the merge gives an unchanged image its stored node back.
    /// </summary>
    public static JsonObject ImageNode(string src, string alt, string? title)
    {
        return new JsonObject
        {
            ["type"] = "image",
            ["attrs"] = new JsonObject
            {
                ["src"] = src, ["alt"] = alt, ["title"] = string.IsNullOrEmpty(title) ? null : title, ["width"] = null, ["height"] = null, ["presentation"] = null,
            },
        };
    }

    /// <summary>
    /// The editor's display math needs its closing <c>$$</c> before a blank line, which a Markdig block parser cannot look ahead
    /// for: the lines of a <c>$$</c> left unclosed (or empty) are parsed again as ordinary Markdown, as the editor reads them.
    /// </summary>
    private static MarkdownDocument ParseDocument(string markdown, Dialect dialect)
    {
        for (var pass = 0; ; pass++)
        {
            var context = new MarkdownParserContext();
            context.Properties[typeof(Dialect)] = dialect;
            dialect.Unmatched = [default, default];
            var document = Markdig.Markdown.Parse(markdown, Pipeline, context);
            var failed = dialect.Math && pass < MaxMathPasses ? document.Descendants<MathBlock>().Where(x => !x.Complete).ToList() : [];
            if (failed.Count == 0)
            {
                return document;
            }
            foreach (var block in failed)
            {
                // No $$ among these lines finds a closer either: none of them ends with $$, and the same line ends the search.
                for (var line = block.Line; line <= block.LastLine; line++)
                {
                    dialect.Excluded.Add(line);
                }
            }
        }
    }

    /// <summary>A plugin component the editor's validator accepts (its <c>normalize</c>), or null.</summary>
    private static JsonObject? Component(string type, JsonObject data)
    {
        return Refusal(type, data) is null ? new JsonObject { ["type"] = type, ["attrs"] = new JsonObject { ["data"] = data } } : null;
    }

    /// <summary>Why the editor's validator refuses a plugin component, or null when it accepts it.</summary>
    private static string? Refusal(string type, JsonObject data)
    {
        var node = new JsonObject { ["type"] = type, ["attrs"] = new JsonObject { ["data"] = data.DeepClone() } };
        var block = type is "mathInline" or "contactInline" ? RichNodes.Node("paragraph", [node]) : node;
        return HostDocumentValidator.Instance.Validate(
            $$$"""{"format":"{{{MarkdownDocumentValidator.Format}}}","version":1,"plugins":{{{ValidatorPlugins}}},"document":{"type":"doc","content":[{{{block.ToJsonString()}}}]}}""")?.Message;
    }

    private static JsonObject MathData(string expression)
    {
        return new JsonObject { ["version"] = 1, ["expression"] = expression };
    }

    private static string JsTrim(string value)
    {
        return value.Trim(EditorMarkdownWriter.JsTrimSpace);
    }

    private static string JsTrimEnd(string value)
    {
        return value.TrimEnd(EditorMarkdownWriter.JsTrimSpace);
    }

    private static bool Blank(string value)
    {
        return value.AsSpan().Trim(EditorMarkdownWriter.JsTrimSpace).IsEmpty;
    }

    private static string CanonicalAttachment(Match match, string kind)
    {
        return "/api/attachments/" + Guid.Parse(match.Groups["id"].Value).ToString("D") + "/" + kind;
    }

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough | EmphasisExtraOptions.Marked)
            .UseTaskLists();
        builder.InlineParsers.InsertBefore<EscapeInlineParser>(new PlaceholderParser());
        builder.InlineParsers.InsertBefore<EscapeInlineParser>(new MathInlineParser());
        builder.InlineParsers.InsertBefore<LinkInlineParser>(new PageLinkParser());
        builder.BlockParsers.InsertBefore<FencedCodeBlockParser>(new MathBlockParser());
        return builder.Build();
    }

    private sealed class Conversion(string source, Dialect dialect, Func<string> newId, IReadOnlySet<string>? plugins)
    {
        private readonly List<string> m_warnings = [];
        private readonly HashSet<TaskList> m_taskMarkers = [];
        private bool m_inTable;
        private string[]? m_lines;

        // Tables a chart links to by their {#id} paragraph (which is no content), the tables read with each id, and the charts
        // read once the whole document is: a chart may stand before its table.
        private readonly Dictionary<Table, string> m_linkedTables = [];
        private readonly HashSet<ParagraphBlock> m_idParagraphs = [];
        private readonly Dictionary<string, JsonObject> m_tablesById = new(StringComparer.Ordinal);
        private readonly List<(JsonObject Node, string Body, int Line, int Column)> m_charts = [];

        public List<DialectIssue> Errors { get; } = [];

        /// <summary>The attribute blocks of heading lines by line index (<see cref="ReadHeadingAttributes"/>).</summary>
        public Dictionary<int, JsonObject> HeadingAttributes { get; init; } = [];

        /// <summary>An unknown or invalid component (<paramref name="invalid"/>: an error in strict mode) or a warning about one.</summary>
        public void Issue(bool invalid, int line, int column, string construct, string message)
        {
            var issue = new DialectIssue(line, column, construct, message);
            if (invalid)
            {
                Errors.Add(issue);
            }
            Warn(char.ToUpperInvariant(issue.Describe()[0]) + issue.Describe()[1..]);
        }

        /// <summary>1-based line and column of a construct whose source line holds <paramref name="markup"/> where it starts.</summary>
        private (int Line, int Column) Position(int line, string markup)
        {
            m_lines ??= source.Split('\n');
            var text = line >= 0 && line < m_lines.Length ? m_lines[line] : "";
            return (line + 1, Math.Max(0, text.IndexOf(markup, StringComparison.Ordinal)) + 1);
        }

        /// <summary>
        /// markdownImportSyntax.ts <c>linkChartTables</c>: a table directly followed by a paragraph exactly <c>{#id}</c> carries that id when
        /// a ```a-chart of the same Markdown names it (the first such table per id); the paragraph is no content then, and the table's
        /// cells get new ids the chart reads. Any other <c>{#…}</c> stays text.
        /// </summary>
        public void LinkChartTables(MarkdownDocument document)
        {
            if (!dialect.Charts)
            {
                return;
            }
            var named = document.Descendants<FencedCodeBlock>().Where(x => x.Info == MarkdownDialect.Chart)
                .Select(x => MarkdownDialect.ChartTableId(x.Lines.ToString())).OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (named.Count == 0)
            {
                return;
            }
            foreach (var table in document.Descendants<Table>().ToList())
            {
                var parent = table.Parent;
                var index = parent?.IndexOf(table) ?? -1;
                if (parent is null || index + 1 >= parent.Count || parent[index + 1] is not ParagraphBlock paragraph)
                {
                    continue;
                }
                var text = paragraph.Span.Start >= 0 && paragraph.Span.End < source.Length ? JsTrim(source.Substring(paragraph.Span.Start, paragraph.Span.Length)) : "";
                if (MarkdownDialect.IdParagraph().Match(text) is not { Success: true } match || !MarkdownDialect.Id().IsMatch(match.Groups[1].Value)
                    || !named.Remove(match.Groups[1].Value))
                {
                    continue;
                }
                m_linkedTables[table] = match.Groups[1].Value;
                m_idParagraphs.Add(paragraph);
            }
        }

        /// <summary>
        /// The ```a-chart fences read (charts.ts <c>readChartFence</c>), now that every linked table is: valid data replaces the code
        /// block standing in for the chart; an invalid one stays that code block and is an issue.
        /// </summary>
        public void ReadCharts()
        {
            foreach (var (node, body, line, column) in m_charts)
            {
                string reason;
                try
                {
                    var data = MarkdownDialect.ReadChart(body, id => m_tablesById.GetValueOrDefault(id),
                        warning => Issue(false, line, column, MarkdownDialect.Chart, warning));
                    if (Refusal("chart", data) is not { } refusal)
                    {
                        node.Clear();
                        node["type"] = "chart";
                        node["attrs"] = new JsonObject { ["data"] = data };
                        continue;
                    }
                    reason = refusal;
                }
                catch (MarkdownDialect.DialectException error)
                {
                    reason = error.Message;
                }
                Issue(true, line, column, MarkdownDialect.Chart, MarkdownDialect.InvalidComponent(MarkdownDialect.Chart, reason));
            }
        }

        /// <summary>The paragraph's first source line: an escaped <c>\[!NOTE\]</c> parses to the same literals as a marker.</summary>
        private string FirstLine(ParagraphBlock paragraph)
        {
            var start = paragraph.Span.Start;
            if (start < 0 || start >= source.Length)
            {
                return "";
            }
            var end = source.IndexOf('\n', start);
            return source[start..(end < 0 ? source.Length : end)].TrimEnd('\r');
        }

        public IReadOnlyList<string> Warnings => m_warnings;

        private void Warn(string warning)
        {
            if (!m_warnings.Contains(warning))
            {
                m_warnings.Add(warning);
            }
        }

        public JsonArray Blocks(IEnumerable<Block> container, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ApiException(400, "Markdown je příliš hluboce vnořený.", "invalid_markdown");
            }
            var result = new JsonArray();
            foreach (var block in container)
            {
                switch (block)
                {
                    case HeadingBlock heading:
                    {
                        var attributes = new JsonObject { ["level"] = heading.Level };
                        foreach (var (name, value) in HeadingAttributes.GetValueOrDefault(heading.Line) ?? [])
                        {
                            attributes[name] = value?.DeepClone();
                        }
                        result.Add(RichNodes.Node("heading", Inlines(heading.Inline, [], depth + 1), attributes));
                        break;
                    }
                    case ParagraphBlock paragraph when m_idParagraphs.Contains(paragraph):
                        // The {#id} of a table a chart links to: the table's attribute.
                        break;
                    case ParagraphBlock paragraph when depth == 0 && dialect.Requirements && paragraph.Span.Start >= 0 && paragraph.Span.Start < source.Length
                        && source.AsSpan(paragraph.Span.Start).StartsWith(RequirementHeadingMarkdown.DependencyLabel, StringComparison.Ordinal):
                    {
                        // „Závislosti: [A](…), …“ (an escaped label stays a note): the dependency line of the section, only directly under a
                        // requirement heading (its fixed place) or as the first block of a fragment (a section's line read alone, the
                        // projection's round trip of one block); under a plain heading or after another line it is a note as written.
                        var parsed = new JsonArray();
                        Paragraphs(paragraph.Inline, depth, parsed);
                        if (parsed is [JsonObject single] && (result.Count == 0 || RequirementHeadings.IsRequirement(result[^1]))
                            && RequirementHeadingMarkdown.DependencyLine(single) is { } line)
                        {
                            result.Add(line);
                        }
                        else
                        {
                            RichNodes.Append(result, parsed);
                        }
                        break;
                    }
                    case ParagraphBlock paragraph:
                        Paragraphs(paragraph.Inline, depth, result);
                        break;
                    case MathBlock math:
                        result.Add(math.Complete && Component("mathBlock", MathData(JsTrim(string.Join('\n', math.Content)))) is { } formula
                            ? formula
                            // As in the editor, an invalid formula leaves its source lines as text.
                            : RichNodes.Node("paragraph", Lines(string.Join('\n', math.Source))));
                        break;
                    case FencedCodeBlock code when dialect.Components && dialect.Contacts && code.Info == MarkdownDialect.Contact:
                        // A ```a-contact fence listing several people is a card each (one contact per block, Atlas 3.2): split as the
                        // editor splits it, before validation, so one invalid item or a list over the limit refuses only its own part.
                        foreach (var (part, offset) in ContactCardSplit.SequenceItems(code.Lines.ToString()))
                        {
                            result.Add(Fenced(code, part, offset));
                        }
                        break;
                    case FencedCodeBlock code:
                        result.Add(Fenced(code, depth: depth));
                        break;
                    case CodeBlock code:
                        result.Add(RichNodes.Node("codeBlock", RichNodes.Text(code.Lines.ToString())));
                        break;
                    case QuoteBlock quote:
                        result.Add(Quote(quote, depth));
                        break;
                    case ListBlock list:
                        List(list, depth, result);
                        break;
                    case Table table:
                        if (TableNode(table, depth) is { } converted)
                        {
                            result.Add(converted);
                        }
                        break;
                    case ThematicBreakBlock:
                        result.Add(RichNodes.Node("horizontalRule"));
                        break;
                    case HtmlBlock html:
                        Warn("HTML je převedené na text.");
                        result.Add(RichNodes.Node("paragraph", Lines(html.Lines.ToString())));
                        break;
                    case LinkReferenceDefinitionGroup or LinkReferenceDefinition:
                        // Parsed structure, not visible content.
                        break;
                    case LeafBlock leaf:
                        result.Add(RichNodes.Node("paragraph", Lines(leaf.Lines.ToString())));
                        break;
                    case ContainerBlock other:
                        RichNodes.Append(result, Blocks(other, depth + 1));
                        break;
                }
            }
            return result;
        }

        /// <summary>
        /// A fence as the editor reads it: ```math and ```mermaid as its components (code without the plugin, or for data its validator
        /// refuses). The <c>a-</c> namespace is reserved (§2.5): ```a-contact and ```a-chart are components; any other <c>a-</c> name, a
        /// component without its plugin and invalid data stay a code block with that language and are an issue; ```a-code &lt;language&gt;
        /// is a code block in that language. <paramref name="body"/>: one item of a ```a-contact fence, <paramref name="offset"/> lines in.
        /// </summary>
        private JsonObject Fenced(FencedCodeBlock code, string? body = null, int offset = 0, int depth = 0)
        {
            var text = body ?? code.Lines.ToString();
            var info = code.Info ?? "";
            JsonObject Code(string language) => RichNodes.Node("codeBlock", RichNodes.Text(text), language.Length > 0 ? new JsonObject { ["language"] = language } : null);
            if (dialect.Components && info == "a-code")
            {
                return Code((code.Arguments ?? "").Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");
            }
            if (dialect.Components && info.StartsWith("a-", StringComparison.Ordinal))
            {
                var (line, column) = Position(code.Line, new string(code.FencedChar, code.OpeningFencedCharCount));
                line += offset;
                var fallback = Code(info);
                if (!MarkdownDialect.Name().IsMatch(info) || !(info == MarkdownDialect.Table || (info == MarkdownDialect.Contact ? dialect.Contacts
                    : info == MarkdownDialect.Overview ? dialect.Overviews : info == MarkdownDialect.Chart && dialect.Charts)))
                {
                    Issue(true, line, column, info, MarkdownDialect.UnknownComponent(info));
                    return fallback;
                }
                if (info == MarkdownDialect.Table)
                {
                    try
                    {
                        return TableFence(text, depth, line, column);
                    }
                    catch (MarkdownDialect.DialectException error)
                    {
                        Issue(true, line, column, info, MarkdownDialect.InvalidComponent(info, error.Message));
                        return fallback;
                    }
                }
                if (info == MarkdownDialect.Overview)
                {
                    // The overview stores only its kind (and the id the editor gives it); what it shows is computed (overviewKinds.ts).
                    if (MarkdownDialect.ReadOverview(text) is { } kind)
                    {
                        return RichNodes.Node("overview", null, new JsonObject { ["kind"] = kind });
                    }
                    Issue(true, line, column, info, MarkdownDialect.InvalidComponent(info, "Přehled má jediný klíč kind, např. kind: contacts."));
                    return fallback;
                }
                if (info == MarkdownDialect.Chart)
                {
                    m_charts.Add((fallback, text, line, column));
                    return fallback;
                }
                string reason;
                try
                {
                    var data = MarkdownDialect.ReadContacts(text);
                    if (Refusal("contactCard", data) is not { } refusal)
                    {
                        return Component("contactCard", MarkdownDialect.NormalizeContacts(data))!;
                    }
                    reason = refusal;
                }
                catch (MarkdownDialect.DialectException error)
                {
                    reason = error.Message;
                }
                Issue(true, line, column, info, MarkdownDialect.InvalidComponent(info, reason));
                return fallback;
            }
            return info.ToLowerInvariant() switch
            {
                "math" when dialect.Math => Component("mathBlock", MathData(JsTrim(text))),
                "mermaid" when dialect.Diagrams => Component("diagram", new JsonObject { ["version"] = 1, ["source"] = text, ["caption"] = "" }),
                _ => null,
            } ?? Code(info.Trim());
        }

        /// <summary>
        /// Inlines of a paragraph as a paragraph; an attachment image standing in it (not inside a link or emphasis) is an image
        /// block of its own, since the editor's image is a block: the text before and after it stays paragraphs, trimmed.
        /// </summary>
        private void Paragraphs(IEnumerable<Inline>? inlines, int depth, JsonArray result)
        {
            var all = (inlines ?? []).ToList();
            if (!all.Any(x => EmbeddedImage(x) is not null))
            {
                result.Add(RichNodes.Node("paragraph", Inlines(all, [], depth + 1)));
                return;
            }
            var run = new List<Inline>();
            foreach (var inline in all.Append(null))
            {
                var image = inline is null ? null : EmbeddedImage(inline);
                if (inline is not null && image is null)
                {
                    run.Add(inline);
                    continue;
                }
                if (Trimmed(Inlines(run, [], depth + 1)) is { Count: > 0 } text)
                {
                    result.Add(RichNodes.Node("paragraph", text));
                }
                run.Clear();
                if (image is not null)
                {
                    result.Add(image);
                }
            }
        }

        private static JsonObject? EmbeddedImage(Inline inline)
        {
            return inline is LinkInline { IsImage: true } link && AttachmentImageSrc(link.Url ?? "") is { } src ? ImageNode(src, PlainText(link), link.Title) : null;
        }

        /// <summary>The alternative text of an image: the text of its inlines without marks.</summary>
        private static string PlainText(ContainerInline container)
        {
            var text = new System.Text.StringBuilder();
            foreach (var inline in container)
            {
                text.Append(inline switch
                {
                    LiteralInline literal => literal.Content.ToString(),
                    CodeInline code => code.Content,
                    HtmlEntityInline entity => entity.Transcoded.ToString(),
                    LineBreakInline => " ",
                    ContainerInline child => PlainText(child),
                    _ => "",
                });
            }
            return text.ToString();
        }

        /// <summary>Leading and trailing whitespace and line breaks of a paragraph cut by an image.</summary>
        private static JsonArray Trimmed(JsonArray inlines)
        {
            foreach (var atStart in new[] { true, false })
            {
                while (inlines.Count > 0)
                {
                    var index = atStart ? 0 : inlines.Count - 1;
                    var node = inlines[index]!;
                    if (RichDocument.TypeOf(node) == "hardBreak")
                    {
                        inlines.RemoveAt(index);
                        continue;
                    }
                    if (RichDocument.TypeOf(node) != "text")
                    {
                        break;
                    }
                    var text = node["text"]!.GetValue<string>();
                    var trimmed = atStart ? text.TrimStart() : text.TrimEnd();
                    if (trimmed.Length > 0)
                    {
                        node["text"] = trimmed;
                        break;
                    }
                    inlines.RemoveAt(index);
                }
            }
            return inlines;
        }

        private JsonObject Quote(QuoteBlock quote, int depth)
        {
            // A GitHub alert: the quote's first line is exactly [!NOTE] … [!CAUTION], as the editor writes a callout.
            if (quote.FirstOrDefault() is ParagraphBlock first
                && AlertMarker().Match(FirstLine(first)) is { Success: true } marker
                && CalloutVariants.TryGetValue(marker.Groups["kind"].Value, out var variant))
            {
                var content = new JsonArray();
                var rest = (first.Inline ?? []).SkipWhile(x => x is not LineBreakInline).Skip(1).ToList();
                if (rest.Count > 0)
                {
                    Paragraphs(rest, depth, content);
                }
                RichNodes.Append(content, Blocks(quote.Skip(1), depth + 1));
                return RichNodes.Node("callout", RichNodes.NonEmpty(content), new JsonObject { ["variant"] = variant });
            }
            return RichNodes.Node("blockquote", RichNodes.NonEmpty(Blocks(quote, depth + 1)));
        }

        private void List(ListBlock list, int depth, JsonArray result)
        {
            var items = list.OfType<ListItemBlock>().ToList();
            var tight = !list.IsLoose;
            if (list.IsOrdered)
            {
                var ordered = new JsonArray();
                foreach (var item in items)
                {
                    ordered.Add(RichNodes.Node("listItem", RichNodes.LeadingParagraph(Blocks(item, depth + 1))));
                }
                result.Add(RichNodes.Node("orderedList", ordered, new JsonObject
                {
                    ["tight"] = tight,
                    ["start"] = int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? start : 1,
                }));
                return;
            }
            // A task list holds only task items, so a mixed list becomes consecutive lists.
            var index = 0;
            while (index < items.Count)
            {
                var tasks = TaskOf(items[index]) is not null;
                var run = new JsonArray();
                while (index < items.Count && TaskOf(items[index]) is not null == tasks)
                {
                    var item = items[index++];
                    if (TaskOf(item) is { } marker)
                    {
                        m_taskMarkers.Add(marker);
                    }
                    var content = RichNodes.LeadingParagraph(Blocks(item, depth + 1));
                    if (TaskOf(item) is { } task)
                    {
                        RemoveTaskMarker(content);
                        run.Add(RichNodes.Node("taskItem", content, new JsonObject { ["checked"] = task.Checked }));
                    }
                    else
                    {
                        run.Add(RichNodes.Node("listItem", content));
                    }
                }
                // A bullet list whose first item starts with a stamp „3. 11. 2026 17:19 — “ is a timed list (rich revision 5), as the editor reads it.
                var timed = !tasks && LegacyDocumentRepair.Stamped(run[0]);
                if (timed)
                {
                    foreach (var item in run.OfType<JsonObject>())
                    {
                        item["type"] = "timedItem";
                    }
                }
                result.Add(tasks ? RichNodes.Node("taskList", run) : RichNodes.Node(timed ? "timedList" : "bulletList", run, new JsonObject { ["tight"] = tight }));
            }
        }

        private static TaskList? TaskOf(ListItemBlock item)
        {
            return item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList task } ? task : null;
        }

        /// <summary>The task marker became a literal at the start of the item's paragraph; the box is the checked attribute.</summary>
        private static void RemoveTaskMarker(JsonArray content)
        {
            if (content[0]?["content"] is not JsonArray inlines)
            {
                return;
            }
            if (inlines.Count > 0 && RichDocument.TypeOf(inlines[0]) == "text" && inlines[0]!["marks"] is null)
            {
                var text = inlines[0]!["text"]!.GetValue<string>().TrimStart();
                if (text.Length == 0)
                {
                    inlines.RemoveAt(0);
                }
                else
                {
                    inlines[0]!["text"] = text;
                }
            }
            if (inlines.Count == 0)
            {
                content[0]!.AsObject().Remove("content");
            }
        }

        /// <summary>A ```a-table (§4.12): each cell's Markdown read as blocks one level down; its issues are the fence's.</summary>
        private JsonObject TableFence(string body, int depth, int line, int column)
        {
            var rows = new JsonArray();
            foreach (var cells in MarkdownDialect.ReadTable(body))
            {
                var row = new JsonArray();
                foreach (var (type, attributes, markdown) in cells)
                {
                    var cell = Convert(markdown, plugins, newId, depth + 1, out var blocks);
                    Errors.AddRange(cell.Errors.Select(error => error with { Line = line, Column = column }));
                    foreach (var warning in cell.Warnings)
                    {
                        Warn(warning);
                    }
                    row.Add(RichNodes.Node(type, RichNodes.NonEmpty(blocks), attributes));
                }
                rows.Add(RichNodes.Node("tableRow", row));
            }
            return RichNodes.Node("table", rows);
        }

        private JsonObject? TableNode(Table table, int depth)
        {
            var rows = table.OfType<TableRow>().ToList();
            var width = rows.Count == 0 ? 0 : rows[0].OfType<TableCell>().Count();
            if (width == 0)
            {
                return null;
            }
            var content = new JsonArray();
            var linked = m_linkedTables.GetValueOrDefault(table);
            m_inTable = true;
            try
            {
                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    // GFM: the header fixes the column count; missing cells are empty, excess cells are dropped.
                    var cells = rows[rowIndex].OfType<TableCell>().Take(width).ToList();
                    var row = new JsonArray();
                    for (var column = 0; column < width; column++)
                    {
                        var alignment = column < table.ColumnDefinitions.Count ? table.ColumnDefinitions[column].Alignment switch
                        {
                            TableColumnAlign.Left => "left",
                            TableColumnAlign.Center => "center",
                            TableColumnAlign.Right => "right",
                            _ => null,
                        } : null;
                        var body = column < cells.Count ? Blocks(cells[column], depth + 1) : [];
                        var attributes = alignment is null ? null : new JsonObject { ["textAlign"] = alignment, ["align"] = alignment };
                        if (linked is not null)
                        {
                            attributes ??= [];
                            attributes["chartRef"] = newId();
                        }
                        row.Add(RichNodes.Node(rows[rowIndex].IsHeader ? "tableHeader" : "tableCell", RichNodes.NonEmpty(body), attributes));
                    }
                    content.Add(RichNodes.Node("tableRow", row));
                }
            }
            finally
            {
                m_inTable = false;
            }
            if (linked is null)
            {
                return RichNodes.Node("table", content);
            }
            var node = RichNodes.Node("table", content, new JsonObject { ["chartRef"] = linked });
            m_tablesById[linked] = node;
            return node;
        }

        private JsonArray Inlines(IEnumerable<Inline>? inlines, List<JsonObject> marks, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ApiException(400, "Markdown je příliš hluboce vnořený.", "invalid_markdown");
            }
            var result = new JsonArray();
            foreach (var inline in inlines ?? [])
            {
                switch (inline)
                {
                    case PageLinkInline page:
                        result.Add(new JsonObject { ["type"] = PageLinkNodeType, ["attrs"] = new JsonObject { ["title"] = page.Title } });
                        break;
                    case MathInline math:
                    {
                        // The editor reads a table cell's \| as a pipe before the formula.
                        var content = m_inTable ? math.Content.Replace("\\|", "|", StringComparison.Ordinal) : math.Content;
                        if (Component("mathInline", MathData(JsTrim(content))) is { } formula)
                        {
                            result.Add(RichNodes.WithMarks(formula, marks));
                        }
                        else
                        {
                            AddText(result, LineBreakRun().Replace(math.Markup + content + math.Markup, " "), marks);
                        }
                        break;
                    }
                    case SlotInline slot:
                        result.Add(RichNodes.WithMarks(new JsonObject { ["type"] = SlotNodeType, ["attrs"] = new JsonObject { ["index"] = slot.Index } }, marks));
                        break;
                    case LiteralInline literal:
                        AddText(result, literal.Content.ToString(), marks);
                        break;
                    case CodeInline code:
                        AddText(result, code.Content, [.. marks, RichNodes.Mark("code")]);
                        break;
                    case LineBreakInline line:
                        if (line.IsHard)
                        {
                            result.Add(RichNodes.Node("hardBreak"));
                        }
                        else
                        {
                            AddText(result, " ", marks);
                        }
                        break;
                    case EmphasisInline emphasis:
                        RichNodes.Append(result, Inlines(emphasis, EmphasisMark(emphasis) is { } mark ? [.. marks, RichNodes.Mark(mark)] : marks, depth + 1));
                        break;
                    case LinkInline link:
                        Link(result, link, marks, depth);
                        break;
                    case AutolinkInline auto:
                    {
                        var href = SafeHref(auto.IsEmail ? "mailto:" + auto.Url : auto.Url);
                        if (href is null)
                        {
                            Warn("Odkaz s nepovoleným schématem je převedený na text.");
                        }
                        AddText(result, auto.Url, href is null ? marks : [.. marks, RichNodes.LinkMark(href, null)]);
                        break;
                    }
                    case HtmlEntityInline entity:
                        AddText(result, entity.Transcoded.ToString(), marks);
                        break;
                    case HtmlInline html:
                        if (m_inTable && LineBreakTag().IsMatch(html.Tag ?? ""))
                        {
                            // The editor writes a line break inside a table cell as <br>.
                            result.Add(RichNodes.Node("hardBreak"));
                        }
                        else
                        {
                            Warn("HTML je převedené na text.");
                            AddText(result, html.Tag ?? "", marks);
                        }
                        break;
                    case TaskList task:
                        // The box of a task item is its checked attribute; anywhere else the marker stays text.
                        if (!m_taskMarkers.Contains(task))
                        {
                            AddText(result, task.Checked ? "[x] " : "[ ] ", marks);
                        }
                        break;
                    case ContainerInline child:
                        RichNodes.Append(result, Inlines(child, marks, depth + 1));
                        break;
                }
            }
            return result;
        }

        private static string? EmphasisMark(EmphasisInline emphasis)
        {
            return (emphasis.DelimiterChar, emphasis.DelimiterCount) switch
            {
                ('*' or '_', 2) => "bold",
                ('*' or '_', 1) => "italic",
                ('~', 2) => "strike",
                ('=', 2) => "highlight",
                _ => null,
            };
        }

        private void Link(JsonArray result, LinkInline link, List<JsonObject> marks, int depth)
        {
            if (dialect.Components && !link.IsImage && DialectLink(link) is { } dialectLink)
            {
                ComponentLink(result, link, dialectLink.Name, dialectLink.Target, dialectLink.Literal, marks);
                return;
            }
            var url = link.Url ?? "";
            var href = SafeHref(url);
            if (link.IsImage)
            {
                // No other embedded images: the reader's browser would fetch a foreign URL.
                Warn(AttachmentImageSrc(url) is null
                    ? "Obrázek z adresy mimo přílohy stránky je převedený na odkaz; vložit lze jen obrázek nahraný ke stránce: ![popis](/api/attachments/{id}/content)."
                    : "Obrázek uvnitř nadpisu, odkazu nebo zvýraznění je převedený na text; obrázek patří do samostatného odstavce.");
            }
            else if (href is null)
            {
                Warn("Odkaz s nepovoleným schématem je převedený na text.");
            }
            var linkMarks = href is null ? marks : [.. marks, RichNodes.LinkMark(href, link.Title)];
            var children = Inlines(link, linkMarks, depth + 1);
            if (children.Count == 0)
            {
                AddText(result, url, linkMarks);
                return;
            }
            RichNodes.Append(result, children);
        }

        /// <summary>
        /// A link the editor's dialect rule takes (markdownImportSyntax.ts <c>dialectLink</c>): <c>[label](a-name:target)</c> without a
        /// title, its target free of parentheses and white space. Read from the source, as Markdig unescapes a link's address.
        /// </summary>
        private (string Name, string Target, string Literal)? DialectLink(LinkInline link)
        {
            if (link.Span.Start < 0 || link.Span.End >= source.Length)
            {
                return null;
            }
            var literal = source.Substring(link.Span.Start, link.Span.Length);
            var open = literal.LastIndexOf('(');
            return literal.EndsWith(')') && open > 1 && literal[open - 1] == ']' && MarkdownDialect.LinkTarget().Match(literal[(open + 1)..^1]) is { Success: true } target
                ? (target.Groups[1].Value, target.Value, literal)
                : null;
        }

        /// <summary>
        /// An inline <c>a-</c> component: <c>[label](a-contact:?…)</c> is a <c>contactInline</c> (the label is not read); any other name,
        /// the contact without its plugin and invalid data stay the literal source as text, and are an issue.
        /// </summary>
        private void ComponentLink(JsonArray result, LinkInline link, string name, string target, string literal, List<JsonObject> marks)
        {
            var line = link.Line + 1;
            var column = link.Column + 1;
            if (name != MarkdownDialect.Contact || !dialect.Contacts)
            {
                Issue(true, line, column, name, MarkdownDialect.UnknownComponent(name));
                // shortcut: a label over several lines of a quote keeps their "> "; the editor drops it. Upgrade if it ever matters.
                AddText(result, LineBreakRun().Replace(literal, " "), marks);
                return;
            }
            string reason;
            try
            {
                var data = MarkdownDialect.ReadContactLink(target);
                if (Refusal("contactInline", data) is not { } refusal)
                {
                    result.Add(RichNodes.WithMarks(Component("contactInline", MarkdownDialect.NormalizeContacts(data))!, marks));
                    return;
                }
                reason = refusal;
            }
            catch (MarkdownDialect.DialectException error)
            {
                reason = error.Message;
            }
            Issue(true, line, column, name, MarkdownDialect.InvalidComponent(name, reason));
            AddText(result, LineBreakRun().Replace(literal, " "), marks);
        }

        private static void AddText(JsonArray result, string text, List<JsonObject> marks)
        {
            if (text.Length == 0)
            {
                return;
            }
            var node = RichNodes.WithMarks(new JsonObject { ["type"] = "text", ["text"] = text }, marks);
            // Adjacent text with equal marks is one node, as ProseMirror keeps it.
            if (result.Count == 0 || !RichDocument.TryMergeText(result[^1], node))
            {
                result.Add(node);
            }
        }
    }

    /// <summary>Source lines as text separated by hard breaks.</summary>
    private static JsonArray Lines(string source)
    {
        var result = new JsonArray();
        foreach (var line in source.TrimEnd('\n').Split('\n'))
        {
            if (result.Count > 0)
            {
                result.Add(RichNodes.Node("hardBreak"));
            }
            if (line.Length > 0)
            {
                result.Add(new JsonObject { ["type"] = "text", ["text"] = line });
            }
        }
        return result;
    }

    /// <summary>A component placeholder in Markdown text.</summary>
    private sealed class SlotInline : LeafInline
    {
        public int Index { get; init; }
    }

    /// <summary><c>⟦c:N⟧</c> as a slot and <c>\⟦</c> / <c>\⟧</c> as literal brackets; any other bracket is a literal.</summary>
    private sealed class PlaceholderParser : InlineParser
    {
        private const int MaxDigits = 6;

        public PlaceholderParser()
        {
            OpeningCharacters = [PlaceholderOpen, '\\'];
        }

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var start = slice.Start;
            if (slice.CurrentChar == '\\')
            {
                var next = slice.PeekChar();
                if (next is not (PlaceholderOpen or PlaceholderClose))
                {
                    return false;
                }
                processor.Inline = new LiteralInline(next.ToString()) { Span = new SourceSpan(processor.GetSourcePosition(start, out var line, out var column), start + 1), Line = line, Column = column };
                slice.Start = start + 2;
                return true;
            }
            var text = slice.Text;
            var position = start + 1;
            if (position + 1 > slice.End || text[position] != 'c' || text[position + 1] != ':')
            {
                return false;
            }
            position += 2;
            var digits = position;
            while (position <= slice.End && position - digits < MaxDigits + 1 && char.IsAsciiDigit(text[position]))
            {
                position++;
            }
            if (position == digits || position - digits > MaxDigits || text[digits] == '0' || position > slice.End || text[position] != PlaceholderClose)
            {
                return false;
            }
            processor.Inline = new SlotInline
            {
                Index = int.Parse(text.AsSpan(digits, position - digits), NumberStyles.None, CultureInfo.InvariantCulture),
                Span = new SourceSpan(processor.GetSourcePosition(start, out var slotLine, out var slotColumn), position),
                Line = slotLine,
                Column = slotColumn,
            };
            slice.Start = position + 1;
            return true;
        }
    }

    /// <summary>What one parse reads of the editor dialect, shared by its block and inline parsers through the parser context.</summary>
    private sealed class Dialect(bool math, bool diagrams)
    {
        public bool Math { get; } = math;

        public bool Diagrams { get; } = diagrams;

        /// <summary>The reserved <c>a-</c> namespace is read (the page's plugins are known): unknown names are issues, <c>a-code</c> is code.</summary>
        public bool Components { get; init; }

        public bool Contacts { get; init; }

        public bool Charts { get; init; }

        /// <summary>The overview block (Atlas's requirements plugin registers it): <c>```a-overview</c>.</summary>
        public bool Overviews { get; init; }

        /// <summary>Requirement headings and the dependency line (the requirements plugin, schema 3): <see cref="RequirementHeadingMarkdown"/>.</summary>
        public bool Requirements { get; init; }

        /// <summary>Lines of an unclosed <c>$$</c> of an earlier pass, where display math does not open.</summary>
        public HashSet<int> Excluded { get; } = [];

        /// <summary>
        /// The last inline scan per delimiter width that found no closer (markdownInput.ts <c>unmatched</c>): a later <c>$</c> of the
        /// same text inside its range cannot close either, so text with many lone <c>$</c> stays linear.
        /// </summary>
        public (string? Text, int Max, int From, int To)[] Unmatched { get; set; } = [default, default];

        private (string? Text, bool Table) m_table;

        /// <summary>
        /// Whether a paragraph's text is a table, where the editor splits rows into cells at every unescaped <c>|</c> before it reads
        /// formulas. shortcut: a second line that is a delimiter row; Markdig decides the table later, while it reads the inlines.
        /// </summary>
        public bool IsTable(string text)
        {
            if (!ReferenceEquals(m_table.Text, text))
            {
                var first = text.IndexOf('\n');
                var second = first < 0 ? -1 : text.IndexOf('\n', first + 1);
                m_table = (text, first >= 0 && DelimiterRow().IsMatch(text.AsSpan(first + 1, (second < 0 ? text.Length : second) - first - 1)));
            }
            return m_table.Table;
        }

        public static Dialect? Of(MarkdownParserContext? context)
        {
            return context?.Properties.TryGetValue(typeof(Dialect), out var value) == true ? value as Dialect : null;
        }
    }

    /// <summary>Display math in <c>$$</c> lines; <see cref="Complete"/> when the editor reads a formula there.</summary>
    private sealed class MathBlock(BlockParser parser) : LeafBlock(parser)
    {
        /// <summary>The formula's lines without the delimiters.</summary>
        public List<string> Content { get; } = [];

        /// <summary>The source lines trimmed at the end: the text an invalid formula leaves.</summary>
        public List<string> Source { get; } = [];

        public bool Closed { get; set; }

        public int LastLine { get; set; }

        public bool Complete => Closed && !Blank(string.Join('\n', Content));
    }

    /// <summary>
    /// markdownInput.ts <c>mathBlock</c>: a line opening with <c>$$</c> (it interrupts a paragraph, quote or list) up to a line ending
    /// with <c>$$</c>, or <c>$$…$$</c> on one line. Display math cannot contain a blank line (Pandoc), so a <c>$$</c> left open is read
    /// again as text (<see cref="ParseDocument"/>).
    /// </summary>
    private sealed class MathBlockParser : BlockParser
    {
        public MathBlockParser()
        {
            OpeningCharacters = ['$'];
        }

        public override BlockState TryOpen(BlockProcessor processor)
        {
            if (processor.IsCodeIndent || Dialect.Of(processor.Context) is not { Math: true } dialect || dialect.Excluded.Contains(processor.LineIndex))
            {
                return BlockState.None;
            }
            var first = processor.Line.ToString();
            if (!first.StartsWith("$$", StringComparison.Ordinal))
            {
                return BlockState.None;
            }
            var opening = JsTrimEnd(first[2..]);
            var block = new MathBlock(this)
            {
                Line = processor.LineIndex, LastLine = processor.LineIndex, Column = processor.Column, Span = new SourceSpan(processor.Start, processor.Line.End),
            };
            block.Source.Add(JsTrimEnd(first));
            if (opening.Length > 2 && opening.EndsWith("$$", StringComparison.Ordinal))
            {
                var content = opening[..^2];
                if (content.Contains("$$", StringComparison.Ordinal) || Blank(content))
                {
                    return BlockState.None;
                }
                block.Content.Add(content);
                block.Closed = true;
                processor.NewBlocks.Push(block);
                return BlockState.BreakDiscard;
            }
            if (opening.Contains("$$", StringComparison.Ordinal))
            {
                return BlockState.None;
            }
            block.Content.Add(opening);
            processor.NewBlocks.Push(block);
            return BlockState.ContinueDiscard;
        }

        public override BlockState TryContinue(BlockProcessor processor, Block block)
        {
            var math = (MathBlock)block;
            var text = processor.Line.ToString().TrimStart(' ', '\t');
            if (Blank(text))
            {
                return BlockState.None;
            }
            var trimmed = JsTrimEnd(text);
            math.Source.Add(trimmed);
            math.LastLine = processor.LineIndex;
            math.UpdateSpanEnd(processor.Line.End);
            if (trimmed.EndsWith("$$", StringComparison.Ordinal))
            {
                math.Content.Add(trimmed[..^2]);
                math.Closed = true;
                return BlockState.BreakDiscard;
            }
            math.Content.Add(text);
            return BlockState.ContinueDiscard;
        }
    }

    /// <summary>A formula in Markdown text.</summary>
    private sealed class MathInline : LeafInline
    {
        public string Content { get; init; } = "";

        public string Markup { get; init; } = "$";
    }

    /// <summary>
    /// markdownInput.ts <c>mathInline</c>: <c>$…$</c> hugging its content on one line, the closing <c>$</c> before no letter or digit
    /// (<c>$5 a $10</c> and <c>$HOME/$USER</c> stay text), and <c>$$…$$</c> in running text; a backslash escapes the next character.
    /// </summary>
    private sealed class MathInlineParser : InlineParser
    {
        public MathInlineParser()
        {
            OpeningCharacters = ['$'];
        }

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            if (Dialect.Of(processor.Context) is not { Math: true } dialect)
            {
                return false;
            }
            var text = slice.Text;
            var start = slice.Start;
            var max = slice.End + 1;
            var display = start + 1 < max && text[start + 1] == '$';
            var width = display ? 2 : 1;
            if (!display && (start + 1 >= max || IsJsSpace(text[start + 1])))
            {
                return false;
            }
            var miss = dialect.Unmatched[width - 1];
            if (ReferenceEquals(miss.Text, text) && miss.Max == max && start > miss.From && start < miss.To)
            {
                return false;
            }
            var table = dialect.IsTable(text);
            var position = start + width;
            var end = -1;
            while (position < max)
            {
                var character = text[position];
                if (character == '\\')
                {
                    position += 2;
                    continue;
                }
                if ((character == '\n' && !display) || (character == '|' && table))
                {
                    break;
                }
                if (character == '$' && (display
                        ? position + 1 < max && text[position + 1] == '$'
                        : !IsJsSpace(text[position - 1]) && !(position + 1 < max && IsLetterOrNumber(text[position + 1]))))
                {
                    end = position;
                    break;
                }
                position++;
            }
            if (end < 0)
            {
                dialect.Unmatched[width - 1] = (text, max, start, position);
                return false;
            }
            var content = text.Substring(start + width, end - start - width);
            if (Blank(content))
            {
                return false;
            }
            processor.Inline = new MathInline
            {
                Content = content,
                Markup = display ? "$$" : "$",
                Span = new SourceSpan(processor.GetSourcePosition(start, out var line, out var column), end + width - 1),
                Line = line,
                Column = column,
            };
            slice.Start = end + width;
            return true;
        }

        private static bool IsJsSpace(char character)
        {
            return EditorMarkdownWriter.JsTrimSpace.Contains(character);
        }

        /// <summary>JavaScript's <c>/[\p{L}\p{N}]/u</c> on one UTF-16 unit.</summary>
        private static bool IsLetterOrNumber(char character)
        {
            return CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
        }
    }

    /// <summary>A page link by title in Markdown text.</summary>
    private sealed class PageLinkInline : LeafInline
    {
        public string Title { get; init; } = "";
    }

    /// <summary>
    /// <c>[[Název]]</c> on one line, without brackets inside: a page link by title. An escaped bracket (<c>\[\[</c>), as the
    /// writer puts every bracket of text, is consumed by the escape parser first and stays text.
    /// </summary>
    private sealed class PageLinkParser : InlineParser
    {
        public PageLinkParser()
        {
            OpeningCharacters = ['['];
        }

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var start = slice.Start;
            var text = slice.Text;
            if (start + 1 > slice.End || text[start + 1] != '[')
            {
                return false;
            }
            var position = start + 2;
            while (position <= slice.End && text[position] is not ('[' or ']' or '\n' or '\r'))
            {
                position++;
            }
            if (position + 1 > slice.End || text[position] != ']' || text[position + 1] != ']')
            {
                return false;
            }
            var title = text.Substring(start + 2, position - start - 2).Trim();
            if (title.Length == 0 || title.Length > MaxPageLinkTitle)
            {
                return false;
            }
            processor.Inline = new PageLinkInline
            {
                Title = title,
                Span = new SourceSpan(processor.GetSourcePosition(start, out var line, out var column), position + 1),
                Line = line,
                Column = column,
            };
            slice.Start = position + 2;
            return true;
        }
    }
}
