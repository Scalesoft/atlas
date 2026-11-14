using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Atlas.Server.Features.ContentImport.Contracts;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>
/// Wiki.js writes one file per page in the format of the editor that authored it, so pages written
/// in its visual editor arrive as HTML; Markdown pages may also embed HTML blocks. This converts the
/// structural subset to Markdown and hands the result to <see cref="WikiMarkdownConverter"/>;
/// interactive and presentational markup has no editor equivalent and is reported per page instead of
/// being silently dropped. The original stays in the ZIP.
/// </summary>
public static class WikiHtmlToMarkdown
{
    private static readonly HtmlParser Parser = new();

    public static ConvertedHtml Convert(string html)
    {
        var renderer = new Renderer();
        using var document = Parser.ParseDocument(html);
        // A leading <script>, <style> or <meta> is parsed into <head>; it is dropped all the same.
        renderer.Blocks(document.Head, 0);
        var markdown = renderer.Blocks(document.Body, 0);
        return new ConvertedHtml(markdown, renderer.Findings.ToArray());
    }

    public static bool IsDropped(string name)
    {
        return Dropped.ContainsKey(name);
    }

    public static string DroppedLabel(string name)
    {
        return Dropped.TryGetValue(name, out var label) ? label : "prvek <" + name + ">";
    }

    // Interactive or presentational elements: dropped as a whole, named in the finding.
    private static readonly Dictionary<string, string> Dropped = new(StringComparer.Ordinal)
    {
        ["script"] = "skript", ["style"] = "vložený styl", ["input"] = "formulářové pole",
        ["select"] = "rozbalovací seznam", ["button"] = "tlačítko", ["textarea"] = "formulářové pole",
        ["form"] = "formulář", ["label"] = "popisek formuláře", ["iframe"] = "vložený rámec",
        ["link"] = "odkaz na styl", ["meta"] = "meta značka", ["noscript"] = "obsah pro vypnutý skript",
        ["svg"] = "SVG kresba", ["canvas"] = "canvas", ["video"] = "video", ["audio"] = "zvuk",
        ["object"] = "vložený objekt", ["embed"] = "vložený objekt", ["map"] = "obrázková mapa",
    };

    private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
        { "h1", "h2", "h3", "h4", "h5", "h6", "p", "pre", "ul", "ol", "dl", "blockquote", "table", "hr", "figure", "details" };

    // Carry no meaning of their own; their children are rendered in place.
    private static readonly HashSet<string> Transparent = new(StringComparer.Ordinal)
    {
        "div", "span", "section", "article", "main", "header", "footer", "aside", "nav", "small",
        "font", "abbr", "center", "big", "tbody", "thead", "tfoot",
        "colgroup", "col", "body", "html", "time", "q", "cite", "var", "samp",
        "address", "summary", "picture", "figcaption", "caption", "wbr",
    };

    private static readonly HashSet<string> CellContainers = new(StringComparer.Ordinal)
        { "li", "td", "th", "dt", "dd", "tr" };

    private static readonly Dictionary<string, string> AlertKinds = new(StringComparer.Ordinal)
    {
        ["is-info"] = "NOTE", ["is-success"] = "TIP", ["is-warning"] = "WARNING", ["is-danger"] = "CAUTION",
    };

    private sealed class Renderer
    {
        public ImportFindings Findings { get; } = new();

        private void Report(string construct, string impact)
        {
            Findings.Add(construct, impact);
        }

        private void Styled(IElement element)
        {
            if (element.HasAttribute("style"))
            {
                Report("html-style", WikiImportFinding.Degraded);
            }
        }

        public string Blocks(INode? container, int depth)
        {
            return string.Join("\n\n", BlockList(container, depth));
        }

        private List<string> BlockList(INode? container, int depth)
        {
            var result = new List<string>();
            if (container is null)
            {
                return result;
            }
            if (depth > 32)
            {
                Report("html-depth", WikiImportFinding.Lost);
                return result;
            }
            var pending = new StringBuilder();
            void Flush()
            {
                var text = pending.ToString().Trim();
                pending.Clear();
                if (text.Length > 0)
                {
                    result.Add(text);
                }
            }
            foreach (var child in container.ChildNodes)
            {
                if (child is not IElement element)
                {
                    if (child.NodeType == NodeType.Text)
                    {
                        pending.Append(Escape(child.TextContent));
                    }
                    continue;
                }
                var name = element.LocalName;
                if (Dropped.ContainsKey(name))
                {
                    Report("html-dropped:" + name, WikiImportFinding.Lost);
                    continue;
                }
                if (WikiHtmlToMarkdown.Blocks.Contains(name) || name is "dt" or "dd")
                {
                    Flush();
                    Styled(element);
                    result.AddRange(Block(element, depth));
                }
                else if (Transparent.Contains(name) && HasBlock(element))
                {
                    Flush();
                    Styled(element);
                    result.AddRange(BlockList(element, depth + 1));
                }
                else
                {
                    pending.Append(InlineOne(element));
                }
            }
            Flush();
            return result;
        }

        private List<string> Block(IElement element, int depth)
        {
            switch (element.LocalName)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                {
                    var body = Collapse(Inline(element));
                    var level = element.LocalName[1] - '0';
                    return body.Length == 0 ? [] : [new string('#', level) + " " + body];
                }
                case "p":
                    return HasBlock(element) ? BlockList(element, depth + 1) : Single(Inline(element).Trim());
                case "hr":
                    return ["---"];
                case "pre":
                    return Single(Fenced(element));
                case "ul" or "ol":
                    return Single(List(element, depth));
                case "dl":
                    return Single(Definitions(element));
                case "blockquote":
                {
                    var inner = BlockList(element, depth + 1);
                    var kind = element.ClassList.Select(x => AlertKinds.GetValueOrDefault(x)).FirstOrDefault(x => x is not null);
                    if (kind is not null)
                    {
                        // GitHub alert marker on the first quoted line, read back as a callout.
                        inner = inner.Count == 0 ? ["[!" + kind + "]"] : ["[!" + kind + "]\n" + inner[0], .. inner.Skip(1)];
                    }
                    var quoted = string.Join("\n>\n", inner.Select(part =>
                        string.Join("\n", part.Split('\n').Select(line => "> " + line))));
                    return Single(quoted.Trim('>', ' ', '\n').Length == 0 ? "" : quoted);
                }
                case "details":
                {
                    // Emitted as HTML the Markdown converter reads as a heading over its blocks.
                    var summary = element.Children.FirstOrDefault(x => x.LocalName == "summary");
                    var title = summary is null ? "" : Collapse(Plain(summary));
                    summary?.Remove();
                    var inner = BlockList(element, depth + 1);
                    var escaped = title.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
                    return ["<details>\n<summary>" + escaped + "</summary>", .. inner, "</details>"];
                }
                case "figure":
                {
                    var parts = BlockList(element, depth + 1);
                    var caption = element.Children.FirstOrDefault(x => x.LocalName == "figcaption");
                    var text = caption is null ? "" : Collapse(Inline(caption));
                    if (text.Length > 0)
                    {
                        parts.Add("*" + text + "*");
                    }
                    return parts;
                }
                case "table":
                    return Single(Table(element));
                default:
                    return BlockList(element, depth + 1);
            }
        }

        private static List<string> Single(string block)
        {
            return block.Trim().Length == 0 ? [] : [block];
        }

        private string Fenced(IElement pre)
        {
            var code = pre.Children.FirstOrDefault(x => x.LocalName == "code") ?? pre;
            var language = (code.ClassName ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x.StartsWith("language-", StringComparison.Ordinal))
                .Select(x => x["language-".Length..])
                .FirstOrDefault(x => x != "plaintext") ?? "";
            var body = Plain(code).Trim('\n');
            var longest = 0;
            var run = 0;
            foreach (var character in body)
            {
                run = character == '`' ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
            var fence = new string('`', Math.Max(3, longest + 1));
            return fence + language + "\n" + body + "\n" + fence;
        }

        private string List(IElement list, int depth)
        {
            var ordered = list.LocalName == "ol";
            var lines = new List<string>();
            var index = 1;
            foreach (var item in list.Children.Where(x => x.LocalName == "li"))
            {
                var marker = ordered ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " : "- ";
                index++;
                var parts = HasBlock(item) ? BlockList(item, depth + 1) : Single(Inline(item).Trim());
                if (parts.Count == 0)
                {
                    continue;
                }
                var pad = new string(' ', marker.Length);
                var block = marker + string.Join("\n" + pad, parts[0].Split('\n'));
                foreach (var part in parts.Skip(1))
                {
                    block += "\n" + string.Join("\n", part.Split('\n').Select(line => line.Length == 0 ? "" : pad + line));
                }
                lines.Add(block);
            }
            return string.Join("\n", lines);
        }

        private string Definitions(IElement list)
        {
            var lines = new List<string>();
            foreach (var item in list.Children)
            {
                var text = Collapse(Inline(item));
                if (text.Length == 0)
                {
                    continue;
                }
                if (item.LocalName == "dt")
                {
                    lines.Add("**" + text + "**");
                }
                else if (item.LocalName == "dd")
                {
                    lines.Add(": " + text);
                }
            }
            return string.Join("\n", lines);
        }

        private string Table(IElement table)
        {
            var rows = new List<(List<string> Cells, List<string?> Alignments, bool Header)>();
            foreach (var row in Rows(table))
            {
                var cells = row.Children.Where(x => x.LocalName is "td" or "th").ToArray();
                if (cells.Length == 0)
                {
                    continue;
                }
                if (cells.Any(x => x.HasAttribute("colspan") || x.HasAttribute("rowspan")))
                {
                    Report("html-table-merged", WikiImportFinding.Degraded);
                }
                var rendered = new List<string>();
                foreach (var cell in cells)
                {
                    string text;
                    if (HasBlock(cell))
                    {
                        Report("html-table-flattened", WikiImportFinding.Degraded);
                        text = Collapse(string.Join(" ", BlockList(cell, 1)));
                    }
                    else
                    {
                        text = Collapse(Inline(cell));
                    }
                    rendered.Add(text.Replace("|", "\\|", StringComparison.Ordinal));
                }
                rows.Add((rendered, cells.Select(Alignment).ToList(), cells.All(x => x.LocalName == "th")));
            }
            if (rows.Count == 0)
            {
                return "";
            }
            var width = rows.Max(x => x.Cells.Count);
            if (!rows[0].Header)
            {
                Report("html-table-header", WikiImportFinding.Degraded);
            }
            var delimiters = Enumerable.Range(0, width).Select(column => (column < rows[0].Alignments.Count ? rows[0].Alignments[column] : null) switch
            {
                "left" => " :--- ",
                "center" => " :---: ",
                "right" => " ---: ",
                _ => " --- ",
            });
            var lines = new List<string>
            {
                Row(rows[0].Cells, width),
                "|" + string.Join("|", delimiters) + "|",
            };
            lines.AddRange(rows.Skip(1).Select(x => Row(x.Cells, width)));
            return string.Join("\n", lines);
        }

        private static string? Alignment(IElement cell)
        {
            var value = cell.GetAttribute("align") ?? "";
            var style = cell.GetAttribute("style") ?? "";
            var marker = style.IndexOf("text-align", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                value = style[marker..].Split(':', 2).ElementAtOrDefault(1)?.Split(';')[0] ?? value;
            }
            value = value.Trim().ToLowerInvariant();
            return value is "left" or "center" or "right" ? value : null;
        }

        private static string Row(List<string> cells, int width)
        {
            var padded = cells.Concat(Enumerable.Repeat("", Math.Max(0, width - cells.Count)));
            return "| " + string.Join(" | ", padded) + " |";
        }

        private static IEnumerable<IElement> Rows(IElement table)
        {
            foreach (var child in table.Children)
            {
                if (child.LocalName == "tr")
                {
                    yield return child;
                }
                else if (child.LocalName is "thead" or "tbody" or "tfoot")
                {
                    foreach (var row in Rows(child))
                    {
                        yield return row;
                    }
                }
            }
        }

        private string Inline(INode container)
        {
            var builder = new StringBuilder();
            foreach (var child in container.ChildNodes)
            {
                builder.Append(InlineOne(child));
            }
            return builder.ToString();
        }

        private string InlineOne(INode node)
        {
            if (node is not IElement element)
            {
                return node.NodeType == NodeType.Text ? Escape(node.TextContent) : "";
            }
            var name = element.LocalName;
            if (Dropped.ContainsKey(name))
            {
                Report("html-dropped:" + name, WikiImportFinding.Lost);
                return "";
            }
            Styled(element);
            switch (name)
            {
                case "br":
                    return "  \n";
                case "strong" or "b":
                    return Emphasise(Inline(element), "**");
                case "em" or "i":
                    return Emphasise(Inline(element), "*");
                case "s" or "del" or "strike":
                    return Emphasise(Inline(element), "~~");
                case "mark":
                    return Emphasise(Inline(element), "==");
                case "u" or "ins":
                    return Emphasise(Inline(element), "++");
                case "code" or "kbd" or "tt":
                {
                    if (name == "kbd")
                    {
                        Report("kbd", WikiImportFinding.Degraded);
                    }
                    var body = Plain(element);
                    return body.Trim().Length == 0 ? "" : "`" + body.Replace("`", "", StringComparison.Ordinal) + "`";
                }
                case "sub" or "sup":
                    Report(name == "sub" ? "subscript" : "superscript", WikiImportFinding.Degraded);
                    return Inline(element);
                case "a":
                {
                    var text = Inline(element).Trim();
                    var href = (element.GetAttribute("href") ?? "").Trim();
                    if (href.Length == 0 || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    {
                        return text;
                    }
                    return "[" + (text.Length == 0 ? Escape(href) : text) + "](" + Url(href) + ")";
                }
                case "img":
                {
                    var source = (element.GetAttribute("src") ?? "").Trim();
                    var alternative = (element.GetAttribute("alt") ?? "").Trim().Replace("]", " ", StringComparison.Ordinal);
                    return source.Length == 0 ? "" : "![" + alternative + "](" + Url(source) + ")";
                }
                default:
                    if (!Transparent.Contains(name) && !CellContainers.Contains(name) && !WikiHtmlToMarkdown.Blocks.Contains(name))
                    {
                        Report("html:" + name, WikiImportFinding.Degraded);
                    }
                    return Inline(element);
            }
        }

        private static string Emphasise(string body, string marker)
        {
            var trimmed = body.Trim();
            if (trimmed.Length == 0)
            {
                return body;
            }
            var lead = char.IsWhiteSpace(body[0]) ? " " : "";
            var tail = char.IsWhiteSpace(body[^1]) ? " " : "";
            return lead + marker + trimmed + marker + tail;
        }

        private string Plain(INode node)
        {
            if (node is not IElement element)
            {
                return node.NodeType == NodeType.Text ? Normalise(node.TextContent) : "";
            }
            if (Dropped.ContainsKey(element.LocalName))
            {
                return "";
            }
            if (element.LocalName == "br")
            {
                return "\n";
            }
            var builder = new StringBuilder();
            foreach (var child in element.ChildNodes)
            {
                builder.Append(Plain(child));
            }
            return builder.ToString();
        }
    }

    private static bool HasBlock(IElement element)
    {
        foreach (var child in element.Children)
        {
            if (Blocks.Contains(child.LocalName) || CellContainers.Contains(child.LocalName))
            {
                return true;
            }
            if (Transparent.Contains(child.LocalName) && HasBlock(child))
            {
                return true;
            }
        }
        return false;
    }

    private static string Normalise(string text)
    {
        return text.Replace('\u00A0', ' ');
    }

    private static string Escape(string text)
    {
        return WikiMarkdownSyntax.Escape(Normalise(text));
    }

    private static string Collapse(string text)
    {
        return string.Join(" ", text.Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Url(string value)
    {
        return value
            .Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);
    }
}
