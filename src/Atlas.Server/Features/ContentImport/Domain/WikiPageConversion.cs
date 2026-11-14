using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.ContentImport.Contracts;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>
/// One page's conversion to editor JSON: the block and inline mapping, heading identities, the
/// endnotes section and the per-page findings. <see cref="WikiMarkdownConverter"/> is the entry point.
/// </summary>
internal sealed partial class WikiPageConversion
{
    private const int MathExpressionLimit = 10_000;
    private const int TableCellLimit = 1_000;
    // How many following sibling blocks a <details> element or a %% comment may span before it counts as unclosed.
    private const int SiblingScanLimit = 500;

    private static readonly HtmlParser HtmlParser = new();

    [GeneratedRegex(@"^\[!(?<kind>[A-Za-z][\w-]*)\](?<fold>[+-]?)\s*(?<title>.*)$")]
    private static partial Regex AlertMarker();

    [GeneratedRegex(@"^\{\s*\.is-(?<kind>info|success|warning|danger)\s*\}$")]
    private static partial Regex WikiJsCalloutClass();

    [GeneratedRegex(@"^\{\s*[.#][^{}]*\}$")]
    private static partial Regex WikiJsAttributeLine();

    [GeneratedRegex(@"(^|\s)\^[A-Za-z0-9-]+\s*$")]
    private static partial Regex ObsidianBlockId();

    [GeneratedRegex(@"^\s*(>\s?)+")]
    private static partial Regex QuotePrefix();

    [GeneratedRegex(@"^\s*<details[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex DetailsOpen();

    [GeneratedRegex(@"<details[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex DetailsTag();

    [GeneratedRegex(@"</details\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex DetailsClose();

    [GeneratedRegex(@"^<(?<close>/?)(?<name>[A-Za-z][A-Za-z0-9-]*)(?<rest>[^>]*)>$", RegexOptions.Singleline)]
    private static partial Regex HtmlTag();

    // Voids never close, so only a dropped element with content hides what follows its opening tag.
    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

    private readonly WikiSourcePage m_page;
    private readonly WikiPageLinks m_links;
    private readonly bool m_outlineOnly;
    private readonly ImportFindings m_findings = new();
    private readonly List<(string Text, string Id)> m_headings = [];
    private readonly Dictionary<string, int> m_slugs = new(StringComparer.Ordinal);
    private readonly SortedSet<string> m_plugins = new(StringComparer.Ordinal) { "heading-anchors" };
    private readonly HashSet<TaskList> m_consumedTasks = [];
    private readonly Dictionary<ParagraphBlock, (bool Leading, bool Trailing)> m_trim = [];
    private readonly Dictionary<Footnote, string?> m_footnoteSections = [];
    private readonly string m_endnotesId;
    private string m_text;
    private string[] m_lines;
    private int? m_fixedLine;
    private string? m_fixedExcerpt;
    private Block? m_block;
    private Block? m_skip;
    private string? m_currentHeading;
    /// <summary>The level of the last heading converted, for a &lt;details&gt; block's heading.</summary>
    private int? m_level;
    private FootnoteGroup? m_footnotes;

    /// <param name="outlineOnly">Read the heading outline only: no Markdown projection and no document validation.</param>
    public WikiPageConversion(WikiSourcePage page, WikiPageLinks links, bool outlineOnly)
    {
        m_page = page;
        m_links = links;
        m_outlineOnly = outlineOnly;
        m_text = page.Markdown;
        m_lines = page.Markdown.Split('\n');
        m_endnotesId = WikiMarkdownConverter.HeadingId(page.Path, "footnotes");
    }

    public IReadOnlyList<(string Text, string Id)> Headings => m_headings;

    /// <summary>Converts the page; null in outline mode. Throws <see cref="ApiException"/> for a page the editor could not hold.</summary>
    public ImportedDocument? Run()
    {
        var parsed = Markdig.Markdown.Parse(m_page.Markdown, WikiMarkdownConverter.Pipeline);
        if (m_page.DropLeadingHeading && parsed.FirstOrDefault() is HeadingBlock { Level: 1 } leading)
        {
            m_skip = leading;
        }
        var content = Blocks(parsed, 0);
        Endnotes(content);
        if (m_outlineOnly)
        {
            return null;
        }
        // The editor keeps a paragraph after a closing list, table or module; writing it here opens the page unchanged.
        if (content.Count == 0 || RichDocument.TypeOf(content[^1]) != "paragraph")
        {
            content.Add(RichNodes.Node("paragraph"));
        }
        var document = new JsonObject { ["type"] = "doc", ["content"] = content };
        var plugins = new JsonArray();
        foreach (var id in m_plugins)
        {
            plugins.Add(new JsonObject { ["id"] = id, ["version"] = CollaborationSchema.CurrentPluginVersions[id] });
        }
        var root = new JsonObject
        {
            ["format"] = Atlas.Markdown.Sync.MarkdownDocumentValidator.Format,
            ["version"] = 1,
            ["document"] = document,
            ["plugins"] = plugins,
        };
        var findings = new ImportFindings();
        findings.AddRange(m_page.Findings);
        findings.AddRange(m_findings.ToArray());
        var result = new ImportedDocument(root.ToJsonString(RichDocument.JsonWrite), EditorMarkdownWriter.Write(document), findings.ToArray());
        Rules.ValidateDocument(result.ContentJson, result.Markdown);
        return result;
    }

    private int LineIndex(MarkdownObject at)
    {
        // Literals re-created by the link parser carry line 0; fall back to their block.
        return at is Inline && m_block is not null && at.Line < m_block.Line ? m_block.Line : at.Line;
    }

    private (int? Line, string? Excerpt) Location(MarkdownObject? at)
    {
        if (m_fixedLine is not null || m_fixedExcerpt is not null)
        {
            return (m_fixedLine, m_fixedExcerpt);
        }
        var index = at is null ? -1 : LineIndex(at);
        int? line = m_page.SourceIsHtml || index < 0 ? null : index + 1 + m_page.LineOffset;
        return (line, index >= 0 && index < m_lines.Length ? m_lines[index] : null);
    }

    private void Report(string construct, string impact, MarkdownObject? at, string? excerpt = null)
    {
        var (line, source) = Location(at ?? m_block);
        m_findings.Add(construct, impact, line, excerpt ?? source);
    }

    private string Source(Block block)
    {
        var span = block.Span;
        return span.Start >= 0 && span.End < m_text.Length && span.Length > 0 ? m_text.Substring(span.Start, span.Length) : "";
    }

    /// <summary>Source lines of a block without quote markers, for recognising line-level wiki syntax.</summary>
    private string[] RawLines(Block block)
    {
        var source = Source(block);
        return source.Length == 0 ? [] : source.Split('\n').Select(line => QuotePrefix().Replace(line, "").Trim()).ToArray();
    }

    private JsonArray Blocks(IEnumerable<Block> container, int depth)
    {
        if (depth > 32)
        {
            throw new ApiException(400, "Markdown je příliš hluboce vnořený.");
        }
        var blocks = container.ToList();
        var result = new JsonArray();
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (ReferenceEquals(block, m_skip))
            {
                continue;
            }
            m_block = block;
            if (ObsidianCommentEnd(blocks, index) is var end and >= 0)
            {
                // A %% comment separated by blank lines spans sibling blocks; Obsidian hides all of them.
                Report("comment", WikiImportFinding.Lost, block, RawLines(block).FirstOrDefault());
                index = end;
                continue;
            }
            switch (block)
            {
                case HeadingBlock heading:
                    Heading(heading, depth, result);
                    break;
                case ParagraphBlock paragraph:
                    Paragraph(paragraph, depth, result);
                    break;
                case MathBlock math:
                    MathNode(math.Lines.ToString().Trim('\n'), math, result);
                    break;
                case FencedCodeBlock code:
                    Fenced(code, result);
                    break;
                case CodeBlock code:
                    result.Add(RichNodes.Node("codeBlock", RichNodes.Text(code.Lines.ToString())));
                    break;
                case QuoteBlock quote:
                    Quote(quote, depth, result);
                    break;
                case ListBlock list:
                    List(list, depth, result);
                    break;
                case Table table:
                    TableNode(table, depth, result);
                    break;
                case ThematicBreakBlock:
                    result.Add(RichNodes.Node("horizontalRule"));
                    break;
                case HtmlBlock html:
                    index = Html(blocks, index, html, depth, result);
                    break;
                case DefinitionList definitions:
                    Definitions(definitions, depth, result);
                    break;
                case FootnoteGroup group:
                    m_footnotes ??= group;
                    break;
                case LinkReferenceDefinitionGroup:
                case LinkReferenceDefinition:
                    // Link definitions are parsed structure, not visible document content.
                    break;
                default:
                    Report("unsupported-block", WikiImportFinding.Degraded, block);
                    result.Add(RichNodes.Node("codeBlock", RichNodes.Text(Source(block))));
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// The index of the block that closes a <c>%%</c> comment opened, and left open, by the paragraph at
    /// <paramref name="index"/>; -1 when the paragraph opens no such comment or nothing closes it.
    /// </summary>
    private int ObsidianCommentEnd(List<Block> blocks, int index)
    {
        if (blocks[index] is not ParagraphBlock || RawLines(blocks[index]) is not [var first, ..] || !first.StartsWith("%%", StringComparison.Ordinal)
            || Occurrences(Source(blocks[index]), "%%") % 2 == 0)
        {
            return -1;
        }
        for (var next = index + 1; next < blocks.Count && next <= index + SiblingScanLimit; next++)
        {
            if (Occurrences(Source(blocks[next]), "%%") % 2 == 1)
            {
                return next;
            }
        }
        return -1;
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private void Heading(HeadingBlock heading, int depth, JsonArray result)
    {
        var level = heading.Level;
        var inlines = Inlines(heading.Inline, [], depth + 1);
        if (inlines.Count > 0 && RichDocument.TypeOf(inlines[^1]) == "text")
        {
            var text = inlines[^1]!["text"]!.GetValue<string>();
            var stripped = WikiMarkdownConverter.StripAttributes(text);
            if (stripped != text)
            {
                Report("wikijs-attributes", WikiImportFinding.Degraded, heading);
                if (stripped.Length == 0)
                {
                    inlines.RemoveAt(inlines.Count - 1);
                }
                else
                {
                    inlines[^1]!["text"] = stripped;
                }
            }
        }
        // The editor image node is a block; inside a heading only its description can stay.
        foreach (var image in inlines.Where(x => RichDocument.TypeOf(x) == "image").ToArray())
        {
            var alt = image!["attrs"]?["alt"]?.GetValue<string>() ?? "";
            var position = inlines.IndexOf(image);
            inlines.RemoveAt(position);
            if (alt.Length > 0)
            {
                inlines.Insert(position, new JsonObject { ["type"] = "text", ["text"] = alt });
            }
        }
        AddHeading(inlines, level, result);
    }

    private void AddHeading(JsonArray inlines, int level, JsonArray result)
    {
        m_level = level;
        EditorNodes.Normalize(inlines);
        // Keyed on the GitHub-style anchor (slug, then slug-1, slug-2 …), so an edit elsewhere keeps the identity.
        var title = EditorNodes.NodeText(inlines);
        var slug = ImportLinkResolver.Slug(title);
        var occurrence = m_slugs.GetValueOrDefault(slug);
        m_slugs[slug] = occurrence + 1;
        var id = WikiMarkdownConverter.HeadingId(m_page.Path, "heading:" + (occurrence == 0 ? slug : slug + "-" + occurrence.ToString(CultureInfo.InvariantCulture)));
        m_headings.Add((title, id));
        m_currentHeading = id;
        result.Add(RichNodes.Node("heading", inlines, new JsonObject { ["level"] = level, ["headingId"] = id }));
    }

    private void Paragraph(ParagraphBlock paragraph, int depth, JsonArray result)
    {
        var top = paragraph.Inline?.ToList() ?? [];
        var lines = RawLines(paragraph);
        var tocLink = top.Count == 1 && top[0] is WikiLinkInline { IsEmbed: false } wiki
            && (wiki.Target.Equals("_TOC_", StringComparison.OrdinalIgnoreCase) || wiki.Target.Equals("toc", StringComparison.OrdinalIgnoreCase));
        if (tocLink || lines is [var only] && only.Equals("[TOC]", StringComparison.OrdinalIgnoreCase))
        {
            m_plugins.Add("toc");
            result.Add(EditorNodes.Module("tableOfContents", new JsonObject { ["version"] = 1, ["title"] = "Obsah dokumentu", ["minLevel"] = 1, ["maxLevel"] = 3 }));
            return;
        }
        if (top.Count == 1 && top[0] is MathInline { DelimiterCount: 2 } display)
        {
            MathNode(display.Content.ToString(), display, result);
            return;
        }
        var (leading, trailing) = m_trim.GetValueOrDefault(paragraph);
        if (!trailing && lines.Length > 1 && WikiJsAttributeLine().IsMatch(lines[^1]))
        {
            Report("wikijs-attributes", WikiImportFinding.Degraded, paragraph, lines[^1]);
            trailing = true;
        }
        if (leading)
        {
            var firstBreak = top.FindIndex(x => x is LineBreakInline);
            top = firstBreak < 0 ? [] : top[(firstBreak + 1)..];
        }
        if (trailing)
        {
            var lastBreak = top.FindLastIndex(x => x is LineBreakInline);
            top = lastBreak < 0 ? [] : top[..lastBreak];
        }
        if (top.Count == 0 && (leading || trailing))
        {
            return;
        }
        var inlines = Inlines(top, [], depth + 1);
        EditorNodes.Normalize(inlines);
        if (inlines.Count > 0 && RichDocument.TypeOf(inlines[^1]) == "text" && inlines[^1]!["marks"] is null)
        {
            var text = inlines[^1]!["text"]!.GetValue<string>();
            var match = ObsidianBlockId().Match(text);
            if (match.Success)
            {
                Report("obsidian-block-id", WikiImportFinding.Lost, paragraph, match.Value.Trim());
                var rest = text[..match.Index].TrimEnd();
                if (rest.Length == 0)
                {
                    inlines.RemoveAt(inlines.Count - 1);
                }
                else
                {
                    inlines[^1]!["text"] = rest;
                }
            }
        }
        // The editor image node is a block. Split prose around each image without nesting it in a paragraph.
        var run = new JsonArray();
        var emitted = false;
        foreach (var inline in inlines.ToArray())
        {
            inlines.Remove(inline);
            if (RichDocument.TypeOf(inline) == "image")
            {
                EditorNodes.Normalize(run);
                if (run.Count > 0)
                {
                    result.Add(RichNodes.Node("paragraph", run));
                    run = [];
                }
                result.Add(inline);
                emitted = true;
            }
            else
            {
                run.Add(inline);
            }
        }
        EditorNodes.Normalize(run);
        if (run.Count > 0 || !emitted)
        {
            result.Add(RichNodes.Node("paragraph", run));
        }
    }

    private void MathNode(string expression, MarkdownObject at, JsonArray result)
    {
        if (expression.Trim().Length == 0 || expression.Length > MathExpressionLimit)
        {
            Report("math", WikiImportFinding.Degraded, at);
            result.Add(RichNodes.Node("codeBlock", RichNodes.Text(expression), new JsonObject { ["language"] = "latex" }));
            return;
        }
        m_plugins.Add("math");
        result.Add(EditorNodes.Module("mathBlock", new JsonObject { ["version"] = 1, ["expression"] = expression }));
    }

    private void Fenced(FencedCodeBlock code, JsonArray result)
    {
        var info = code.Info ?? "";
        var source = code.Lines.ToString();
        switch (info.Trim().ToLowerInvariant())
        {
            case "mermaid":
                if (WikiMarkdownConverter.IsValidDiagram(source))
                {
                    m_plugins.Add("diagrams");
                    result.Add(EditorNodes.Module("diagram", new JsonObject { ["version"] = 1, ["source"] = source, ["caption"] = "" }));
                    return;
                }
                Report("diagram-invalid", WikiImportFinding.Degraded, code);
                break;
            case "math":
                MathNode(source.Trim('\n'), code, result);
                return;
            case "plantuml" or "puml" or "graphviz" or "dot" or "ditaa" or "d2" or "nomnoml" or "kroki":
                Report("diagram-unsupported", WikiImportFinding.Degraded, code);
                break;
            case "dataview" or "dataviewjs" or "tasks" or "query":
                Report("dynamic-query", WikiImportFinding.Degraded, code);
                break;
        }
        result.Add(RichNodes.Node("codeBlock", RichNodes.Text(source), new JsonObject { ["language"] = info }));
    }

    private void Quote(QuoteBlock quote, int depth, JsonArray result)
    {
        string? variant = null;
        string? title = null;
        if (quote.FirstOrDefault() is ParagraphBlock first && RawLines(first) is [var marker, ..] && AlertMarker().Match(marker) is { Success: true } alert)
        {
            variant = CalloutVariant(alert.Groups["kind"].Value, out var known);
            if (!known)
            {
                Report("callout-kind", WikiImportFinding.Degraded, first, marker);
            }
            if (alert.Groups["fold"].Value.Length > 0)
            {
                Report("callout-fold", WikiImportFinding.Degraded, first, marker);
            }
            title = alert.Groups["title"].Value.Trim();
            m_trim[first] = (true, m_trim.GetValueOrDefault(first).Trailing);
        }
        if (quote.LastOrDefault() is ParagraphBlock last && RawLines(last) is [_, .., var classLine] && WikiJsCalloutClass().Match(classLine) is { Success: true } wikiJs)
        {
            variant ??= CalloutVariant("is-" + wikiJs.Groups["kind"].Value, out _);
            m_trim[last] = (m_trim.GetValueOrDefault(last).Leading, true);
        }
        var content = Blocks(quote, depth + 1);
        if (variant is null)
        {
            result.Add(RichNodes.Node("blockquote", RichNodes.NonEmpty(content)));
            return;
        }
        if (!string.IsNullOrEmpty(title))
        {
            content.Insert(0, RichNodes.Node("paragraph", [new JsonObject { ["type"] = "text", ["text"] = title, ["marks"] = new JsonArray(RichNodes.Mark("bold")) }]));
        }
        result.Add(RichNodes.Node("callout", RichNodes.NonEmpty(content), new JsonObject { ["variant"] = variant }));
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
                if (TaskOf(item) is not null)
                {
                    Report("task-ordered", WikiImportFinding.Degraded, item);
                }
                ordered.Add(RichNodes.Node("listItem", RichNodes.LeadingParagraph(Blocks(item, depth + 1))));
            }
            result.Add(RichNodes.Node("orderedList", ordered, new JsonObject
            {
                ["tight"] = tight,
                ["start"] = int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? start : 1,
            }));
            return;
        }
        // taskList holds only task items, so a mixed Markdown list becomes consecutive lists.
        var index = 0;
        while (index < items.Count)
        {
            var tasks = TaskOf(items[index]) is not null;
            var run = new JsonArray();
            while (index < items.Count && TaskOf(items[index]) is not null == tasks)
            {
                var item = items[index++];
                var task = TaskOf(item);
                if (task is not null)
                {
                    m_consumedTasks.Add(task);
                    run.Add(RichNodes.Node("taskItem", RichNodes.LeadingParagraph(Blocks(item, depth + 1)), new JsonObject { ["checked"] = task.Checked }));
                }
                else
                {
                    run.Add(RichNodes.Node("listItem", RichNodes.LeadingParagraph(Blocks(item, depth + 1))));
                }
            }
            result.Add(tasks ? RichNodes.Node("taskList", run) : RichNodes.Node("bulletList", run, new JsonObject { ["tight"] = tight }));
        }
    }

    private static TaskList? TaskOf(ListItemBlock item)
    {
        return item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList task } ? task : null;
    }

    private void TableNode(Table table, int depth, JsonArray result)
    {
        var rows = table.OfType<TableRow>().ToList();
        var width = rows.Count == 0 ? 0 : rows[0].OfType<TableCell>().Count();
        if (width == 0)
        {
            return;
        }
        if (rows.Count * width > TableCellLimit)
        {
            Report("table-too-large", WikiImportFinding.Degraded, table);
            result.Add(RichNodes.Node("codeBlock", RichNodes.Text(Source(table))));
            return;
        }
        var content = new JsonArray();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            // GFM: the header fixes the column count; missing cells are empty, excess cells are ignored.
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
                var attrs = alignment is null ? null : new JsonObject { ["textAlign"] = alignment, ["align"] = alignment };
                row.Add(RichNodes.Node(rowIndex == 0 ? "tableHeader" : "tableCell", RichNodes.NonEmpty(body), attrs));
            }
            content.Add(RichNodes.Node("tableRow", row));
        }
        result.Add(RichNodes.Node("table", content));
    }

    private int Html(List<Block> blocks, int index, HtmlBlock html, int depth, JsonArray result)
    {
        var source = html.Lines.ToString();
        var trimmed = source.TrimStart();
        if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
        {
            Report("comment", WikiImportFinding.Lost, html);
            // A comment may be followed by further markup in the same HTML block.
            var end = trimmed.IndexOf("-->", StringComparison.Ordinal);
            var rest = end < 0 ? "" : trimmed[(end + 3)..];
            if (rest.Trim().Length > 0)
            {
                HtmlContent(rest, html, depth, result);
            }
            return index;
        }
        if (DetailsOpen().IsMatch(source))
        {
            using var document = HtmlParser.ParseDocument(source);
            var details = document.QuerySelector("details");
            var summary = details?.QuerySelector("summary");
            var title = EditorNodes.Collapse(summary?.TextContent ?? "");
            title = title.Length is 0 or > 500 ? "Podrobnosti" : title;
            summary?.Remove();
            var inner = details?.InnerHtml ?? "";
            if (DetailsClose().IsMatch(source))
            {
                Details(title, result, content => HtmlContent(inner, html, depth + 1, content));
                return index;
            }
            // Markdown inside <details> is separated by blank lines, so the element spans sibling blocks.
            var close = -1;
            var open = 1;
            for (var next = index + 1; next < blocks.Count && next <= index + SiblingScanLimit && close < 0; next++)
            {
                if (blocks[next] is HtmlBlock candidate)
                {
                    var text = candidate.Lines.ToString();
                    open += DetailsTag().Matches(text).Count - DetailsClose().Matches(text).Count;
                    if (open <= 0)
                    {
                        close = next;
                    }
                }
            }
            if (close > 0)
            {
                Details(title, result, content =>
                {
                    if (inner.Trim().Length > 0)
                    {
                        HtmlContent(inner, html, depth + 1, content);
                    }
                    RichNodes.Append(content, Blocks(blocks.GetRange(index + 1, close - index - 1), depth + 1));
                    var tail = DetailsClose().Split(((HtmlBlock)blocks[close]).Lines.ToString())[0];
                    if (tail.Trim().Length > 0)
                    {
                        HtmlContent(tail, blocks[close], depth + 1, content);
                    }
                });
                return close;
            }
        }
        HtmlContent(source, html, depth, result);
        return index;
    }

    /// <summary>A &lt;details&gt; block (Atlas has no disclosure: every heading folds), as LegacyDocumentRepair converts a stored disclosure:
    /// a heading one level below the last one (2–6, 3 with none), converted first, so a nested one goes below it; then its blocks
    /// (<paramref name="convert"/>), after which the level is the one before it again, so the next &lt;details&gt; is its sibling.</summary>
    private void Details(string title, JsonArray result, Action<JsonArray> convert)
    {
        var before = m_level;
        AddHeading(RichNodes.Text(title), Math.Clamp(before is { } above ? above + 1 : 3, 2, 6), result);
        var content = new JsonArray();
        convert(content);
        m_level = before;
        RichNodes.Append(result, content);
    }

    /// <summary>HTML embedded in Markdown takes the same structural conversion as a visual-editor page.</summary>
    private void HtmlContent(string html, Block at, int depth, JsonArray result)
    {
        var converted = WikiHtmlToMarkdown.Convert(html);
        var (line, excerpt) = Location(at);
        m_findings.AddRange(converted.Findings, line, excerpt);
        if (converted.Markdown.Trim().Length == 0)
        {
            return;
        }
        var saved = (m_text, m_lines, m_fixedLine, m_fixedExcerpt, m_block);
        m_text = converted.Markdown;
        m_lines = converted.Markdown.Split('\n');
        m_fixedLine = line;
        m_fixedExcerpt = excerpt ?? "HTML";
        try
        {
            RichNodes.Append(result, Blocks(Markdig.Markdown.Parse(converted.Markdown, WikiMarkdownConverter.Pipeline), depth + 1));
        }
        finally
        {
            (m_text, m_lines, m_fixedLine, m_fixedExcerpt, m_block) = saved;
        }
    }

    private void Definitions(DefinitionList list, int depth, JsonArray result)
    {
        Report("definition-list", WikiImportFinding.Degraded, list);
        foreach (var item in list.OfType<DefinitionItem>())
        {
            foreach (var child in item)
            {
                if (child is DefinitionTerm term)
                {
                    m_block = term;
                    var inlines = Inlines(term.Inline, [RichNodes.Mark("bold")], depth + 1);
                    EditorNodes.Normalize(inlines);
                    result.Add(RichNodes.Node("paragraph", inlines));
                }
                else
                {
                    RichNodes.Append(result, Blocks([child], depth + 1));
                }
            }
        }
    }

    private void Endnotes(JsonArray result)
    {
        var notes = m_footnotes?.OfType<Footnote>().OrderBy(x => x.Order > 0 ? x.Order : int.MaxValue).ToList() ?? [];
        if (notes.Count == 0)
        {
            return;
        }
        Report("footnote", WikiImportFinding.Degraded, notes[0]);
        m_headings.Add((WikiMarkdownConverter.EndnotesTitle, m_endnotesId));
        result.Add(RichNodes.Node("heading", RichNodes.Text(WikiMarkdownConverter.EndnotesTitle), new JsonObject { ["level"] = 2, ["headingId"] = m_endnotesId }));
        var items = new JsonArray();
        foreach (var note in notes)
        {
            var content = Blocks(note, 1);
            var back = m_footnoteSections.TryGetValue(note, out var section) ? m_links.Self(section) : null;
            if (back is not null)
            {
                // The back-reference returns to the section that cites the note; paragraphs have no anchors.
                var link = new JsonObject { ["type"] = "text", ["text"] = "↩", ["marks"] = new JsonArray(RichNodes.LinkMark(back)) };
                if (content.Count > 0 && RichDocument.TypeOf(content[^1]) == "paragraph")
                {
                    var inline = content[^1]!["content"] as JsonArray;
                    if (inline is null)
                    {
                        content[^1]!["content"] = inline = [];
                    }
                    inline.Add(new JsonObject { ["type"] = "text", ["text"] = " " });
                    inline.Add(link);
                    EditorNodes.Normalize(inline);
                }
                else
                {
                    content.Add(RichNodes.Node("paragraph", [link]));
                }
            }
            items.Add(RichNodes.Node("listItem", RichNodes.LeadingParagraph(content)));
        }
        result.Add(RichNodes.Node("orderedList", items, new JsonObject { ["tight"] = true, ["start"] = 1 }));
    }

    private JsonArray Inlines(IEnumerable<Inline>? inlines, List<JsonObject> marks, int depth)
    {
        if (depth > 32)
        {
            throw new ApiException(400, "Markdown je příliš hluboce vnořený.");
        }
        var result = new JsonArray();
        if (inlines is null)
        {
            return result;
        }
        // Inline HTML arrives as separate open/close tags between sibling inlines.
        var html = new List<(string Name, JsonObject? Mark, bool Hide)>();
        List<JsonObject> Current()
        {
            var current = new List<JsonObject>(marks);
            current.AddRange(html.Where(x => x.Mark is not null).Select(x => x.Mark!));
            return current;
        }
        foreach (var inline in inlines)
        {
            // The body of a dropped element (<script>, <style> …) goes with it; only a tag can close it.
            if (inline is not HtmlInline && html.Any(x => x.Hide))
            {
                continue;
            }
            switch (inline)
            {
                case LiteralInline literal:
                    AddText(result, literal.Content.ToString(), Current());
                    break;
                case CodeInline code:
                    AddText(result, code.Content, [.. Current(), RichNodes.Mark("code")]);
                    break;
                case LineBreakInline line:
                    if (line.IsHard)
                    {
                        result.Add(RichNodes.Node("hardBreak"));
                    }
                    else
                    {
                        AddText(result, " ", Current());
                    }
                    break;
                case EmphasisInline emphasis:
                    Emphasis(result, emphasis, Current(), depth);
                    break;
                case LinkInline link:
                    Link(result, link, Current(), depth);
                    break;
                case AutolinkInline auto:
                {
                    var href = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
                    AddText(result, auto.Url, EditorNodes.IsSafeExternal(href) ? [.. Current(), RichNodes.LinkMark(href)] : Current());
                    break;
                }
                case HtmlEntityInline entity:
                    AddText(result, entity.Transcoded.ToString(), Current());
                    break;
                case HtmlInline tag:
                    HtmlTagInline(result, tag, html, Current());
                    break;
                case MathInline math:
                {
                    var expression = math.Content.ToString();
                    if (expression.Trim().Length == 0 || expression.Length > MathExpressionLimit)
                    {
                        Report("math", WikiImportFinding.Degraded, math);
                        var delimiter = new string(math.Delimiter, math.DelimiterCount);
                        AddText(result, delimiter + expression + delimiter, Current());
                    }
                    else
                    {
                        m_plugins.Add("math");
                        result.Add(EditorNodes.Module("mathInline", new JsonObject { ["version"] = 1, ["expression"] = expression }));
                    }
                    break;
                }
                case FootnoteLink footnote:
                    if (!footnote.IsBackLink)
                    {
                        m_footnoteSections.TryAdd(footnote.Footnote, m_currentHeading);
                        var label = "[" + (footnote.Footnote.Order > 0 ? footnote.Footnote.Order.ToString(CultureInfo.InvariantCulture) : footnote.Footnote.Label) + "]";
                        var target = m_links.Self(m_endnotesId);
                        AddText(result, label, target is null ? Current() : [.. Current(), RichNodes.LinkMark(target)]);
                    }
                    break;
                case TaskList task:
                    if (!m_consumedTasks.Contains(task))
                    {
                        AddText(result, task.Checked ? "[x] " : "[ ] ", Current());
                    }
                    break;
                case WikiLinkInline wiki:
                    WikiLink(result, wiki, Current());
                    break;
                case ObsidianCommentInline comment:
                    Report("comment", WikiImportFinding.Lost, comment, comment.Source);
                    break;
                case ContainerInline child:
                    RichNodes.Append(result, Inlines(child, Current(), depth + 1));
                    break;
            }
        }
        return result;
    }

    private void Emphasis(JsonArray result, EmphasisInline emphasis, List<JsonObject> marks, int depth)
    {
        string? mark = (emphasis.DelimiterChar, emphasis.DelimiterCount) switch
        {
            ('*' or '_', >= 2) => "bold",
            ('*' or '_', _) => "italic",
            ('~', 2) => "strike",
            ('=', 2) => "highlight",
            ('+', 2) => "underline",
            _ => null,
        };
        var children = Inlines(emphasis, mark is null ? marks : [.. marks, RichNodes.Mark(mark)], depth + 1);
        if (mark is not null)
        {
            RichNodes.Append(result, children);
            return;
        }
        // Sub/superscript have no editor mark; keep the source notation so the meaning survives.
        Report(emphasis.DelimiterChar == '^' ? "superscript" : "subscript", WikiImportFinding.Degraded, emphasis);
        var delimiter = new string(emphasis.DelimiterChar, emphasis.DelimiterCount);
        AddText(result, delimiter, marks);
        RichNodes.Append(result, children);
        AddText(result, delimiter, marks);
    }

    private void Link(JsonArray result, LinkInline link, List<JsonObject> marks, int depth)
    {
        var url = link.Url ?? "";
        var resolution = m_links.Url(url, link.IsImage);
        var reported = false;
        if (resolution.Finding is not null)
        {
            Report(resolution.Finding, resolution.Impact, link, url);
            reported = true;
        }
        if (link.IsImage && EditorNodes.IsInlineImage(resolution.Href))
        {
            result.Add(RichNodes.Node("image", null, new JsonObject { ["src"] = resolution.Href, ["alt"] = WikiMarkdownConverter.PlainText(link), ["title"] = link.Title }));
            return;
        }
        if (link.IsImage)
        {
            Report("image-unavailable", WikiImportFinding.Degraded, link, url);
            reported = true;
        }
        var href = resolution.Href ?? (EditorNodes.IsSafeExternal(url) ? url : null);
        if (href is null && !reported)
        {
            Report("link-unresolved", WikiImportFinding.Unresolved, link, url);
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

    private void WikiLink(JsonArray result, WikiLinkInline wiki, List<JsonObject> marks)
    {
        var resolution = m_links.Wiki(wiki.Target, wiki.Alias, wiki.IsEmbed);
        if (resolution.Finding is not null)
        {
            Report(resolution.Finding, resolution.Impact, wiki, wiki.Source);
        }
        var label = resolution.Label ?? wiki.Alias ?? wiki.Target;
        if (wiki.IsEmbed && EditorNodes.IsInlineImage(resolution.Href))
        {
            result.Add(RichNodes.Node("image", null, new JsonObject { ["src"] = resolution.Href, ["alt"] = label, ["title"] = null }));
            return;
        }
        AddText(result, label, resolution.Href is null ? marks : [.. marks, RichNodes.LinkMark(resolution.Href)]);
    }

    private void HtmlTagInline(JsonArray result, HtmlInline inline, List<(string Name, JsonObject? Mark, bool Hide)> open, List<JsonObject> marks)
    {
        var tag = inline.Tag ?? "";
        if (tag.StartsWith("<!--", StringComparison.Ordinal))
        {
            Report("comment", WikiImportFinding.Lost, inline, tag);
            return;
        }
        var match = HtmlTag().Match(tag.Trim());
        if (!match.Success)
        {
            // Processing instructions, CDATA and declarations have no visible content.
            Report("html-dropped", WikiImportFinding.Lost, inline, tag);
            return;
        }
        var name = match.Groups["name"].Value.ToLowerInvariant();
        if (match.Groups["close"].Value.Length > 0)
        {
            var index = open.FindLastIndex(x => x.Name == name);
            if (index >= 0)
            {
                open.RemoveAt(index);
            }
            return;
        }
        if (open.Any(x => x.Hide))
        {
            // Markup inside a dropped element goes with it; only the element's closing tag matters.
            return;
        }
        JsonObject? mark = null;
        var hide = false;
        switch (name)
        {
            case "br":
                result.Add(RichNodes.Node("hardBreak"));
                return;
            case "img":
                HtmlImage(result, inline, tag, marks);
                return;
            case "b" or "strong":
                mark = RichNodes.Mark("bold");
                break;
            case "i" or "em":
                mark = RichNodes.Mark("italic");
                break;
            case "s" or "del" or "strike":
                mark = RichNodes.Mark("strike");
                break;
            case "u" or "ins":
                mark = RichNodes.Mark("underline");
                break;
            case "mark":
                mark = RichNodes.Mark("highlight");
                break;
            case "code" or "samp" or "tt":
                mark = RichNodes.Mark("code");
                break;
            case "kbd":
                Report("kbd", WikiImportFinding.Degraded, inline, tag);
                mark = RichNodes.Mark("code");
                break;
            case "sub":
                Report("subscript", WikiImportFinding.Degraded, inline, tag);
                break;
            case "sup":
                Report("superscript", WikiImportFinding.Degraded, inline, tag);
                break;
            case "a":
                mark = HtmlLink(inline, tag);
                break;
            default:
                var dropped = WikiHtmlToMarkdown.IsDropped(name);
                Report(dropped ? "html-dropped:" + name : "html:" + name, dropped ? WikiImportFinding.Lost : WikiImportFinding.Degraded, inline, tag);
                hide = dropped && !VoidElements.Contains(name);
                break;
        }
        if (!match.Groups["rest"].Value.TrimEnd().EndsWith('/'))
        {
            open.Add((name, mark, hide));
        }
    }

    private void HtmlImage(JsonArray result, HtmlInline inline, string tag, List<JsonObject> marks)
    {
        using var document = HtmlParser.ParseDocument(tag);
        var image = document.QuerySelector("img");
        var src = image?.GetAttribute("src")?.Trim() ?? "";
        var alt = image?.GetAttribute("alt")?.Trim() ?? "";
        var resolution = src.Length == 0 ? LinkResolution.None : m_links.Url(src, true);
        if (EditorNodes.IsInlineImage(resolution.Href))
        {
            result.Add(RichNodes.Node("image", null, new JsonObject { ["src"] = resolution.Href, ["alt"] = alt, ["title"] = image?.GetAttribute("title") }));
            return;
        }
        Report("image-unavailable", WikiImportFinding.Degraded, inline, tag);
        AddText(result, alt.Length > 0 ? alt : src, marks);
    }

    private JsonObject? HtmlLink(HtmlInline inline, string tag)
    {
        using var document = HtmlParser.ParseDocument(tag + "</a>");
        var href = document.QuerySelector("a")?.GetAttribute("href")?.Trim() ?? "";
        if (href.Length == 0)
        {
            return null;
        }
        var resolution = m_links.Url(href, false);
        var target = resolution.Href ?? (EditorNodes.IsSafeExternal(href) ? href : null);
        if (resolution.Finding is not null)
        {
            Report(resolution.Finding, resolution.Impact, inline, href);
        }
        else if (target is null)
        {
            Report("link-unresolved", WikiImportFinding.Unresolved, inline, href);
        }
        return target is null ? null : RichNodes.LinkMark(target);
    }

    private void AddText(JsonArray result, string value, List<JsonObject> marks)
    {
        if (value.Length == 0)
        {
            return;
        }
        result.Add(RichNodes.WithMarks(new JsonObject { ["type"] = "text", ["text"] = value }, marks));
    }

    /// <summary>
    /// GitHub and Obsidian alert kinds and Wiki.js classes onto the five editor callout variants: the danger kinds
    /// (caution, danger, error, bug, failure, is-danger) are <c>caution</c>, warning and attention stay <c>warning</c>.
    /// </summary>
    private static string CalloutVariant(string kind, out bool known)
    {
        known = true;
        switch (kind.ToLowerInvariant())
        {
            case "tip" or "hint" or "is-success":
                return "tip";
            case "important":
                return "important";
            case "warning" or "attention" or "is-warning":
                return "warning";
            case "caution" or "danger" or "error" or "failure" or "fail" or "missing" or "bug" or "is-danger":
                return "caution";
            case "note" or "info" or "is-info" or "abstract" or "summary" or "tldr" or "todo" or "example" or "quote" or "cite"
                or "question" or "help" or "faq" or "success" or "check" or "done":
                return "note";
            default:
                known = false;
                return "note";
        }
    }
}
