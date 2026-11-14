using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// Writes the Markdown projection of a rich document exactly as the Atlas editor serializes the same
/// document, so imported, edited and API-written pages share one stored dialect (exports, backups and
/// search read it). This is a port of what the pinned <c>@atlas/markdown</c> (0.9.1; its core serializer
/// is unchanged since 0.7.2) runs: prosemirror-markdown
/// 1.13's <c>MarkdownSerializerState</c>, tiptap-markdown 0.9's delimiter normalisation and node specs,
/// and the editor's own specs, including its Markdown dialect: callouts write a GitHub alert
/// (<c>&gt; [!NOTE]</c> … <c>[!CAUTION]</c>), highlight writes <c>==text==</c>, a diagram without a caption a
/// <c>```mermaid</c> fence and math <c>$$ … $$</c> / <c>```math</c> / <c>$…$</c>. Where the dialect cannot carry
/// the data the editor falls back to its plain-text form (a captioned diagram as "Diagram (Mermaid):" source, math
/// as <c>\[…\]</c> / <c>\(…\)</c>); underline keeps only its text and the
/// table of contents writes its title. GFM tables and task lists stay. Contacts and charts write the dialect's <c>a-contact</c> and
/// <c>a-chart</c> (<see cref="MarkdownDialect"/>; a linked table its <c>{#id}</c> paragraph, a code block in an <c>a-</c> language
/// <c>```a-code</c>), or their readable text where the dialect cannot carry the data. Other Atlas components write what their editor plugins
/// write (<see cref="EditorComponentText"/>): a module (attachment, OpenAPI) its readable text, a page
/// reference and a YouTrack issue a link, an emoji its character, a password reference its <c>::secret{…}</c> /
/// <c>:secret{…}</c> directive (attributes only, never a value), concealed content its blocks and a Projector requirement its task-list line
/// (<see cref="RequirementText"/>). The golden vitest in the web project
/// (<c>src/wikiImportGolden.test.ts</c>) compares this output with the editor's projection, for the import
/// fixtures and for <c>tests/fixtures/content</c> (every text node, every component, merge results); update this class when it reports a
/// difference after an editor upgrade.
/// <para>
/// <see cref="WriteProjection"/> is the same writer for <see cref="RichMarkdownProjection"/>: a component
/// placeholder node (<see cref="MarkdownToRich.SlotNodeType"/>) writes <c>⟦c:N⟧</c>, and a literal <c>⟦</c>
/// or <c>⟧</c> in text is escaped with a backslash, so text never poses as a placeholder.
/// </para>
/// </summary>
public sealed partial class EditorMarkdownWriter
{
    /// <summary>The GitHub alert type the editor writes for each callout variant; an unknown one writes NOTE.</summary>
    private static readonly Dictionary<string, string> AlertTypes = new(StringComparer.Ordinal)
    {
        ["note"] = "NOTE", ["tip"] = "TIP", ["warning"] = "WARNING", ["important"] = "IMPORTANT", ["caution"] = "CAUTION",
    };

    private readonly bool m_projection;
    private readonly JsonObject m_document;
    private HashSet<string>? m_chartTables;
    private IReadOnlyDictionary<string, string>? m_dependencyNames;
    private readonly StringBuilder m_out = new();
    private readonly List<InlineDelimiter> m_inlines = [];
    private string m_delim = "";
    private JsonNode? m_closed;
    private bool m_inAutolink;
    private bool m_atBlockStart;
    private bool m_inTightList;
    private bool m_inTable;

    [GeneratedRegex(@"^(\s*)(.*)$", RegexOptions.Multiline)]
    private static partial Regex LeadingSpace();

    [GeneratedRegex(@"^(.*?)(\s*)$", RegexOptions.Multiline)]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"^(\+[ ]|[\-*>])")]
    private static partial Regex LineStartMarker();

    [GeneratedRegex(@"^(\s*)(#{1,6})(\s|$)")]
    private static partial Regex LineStartHeading();

    [GeneratedRegex(@"^(\s*[0-9]+)\.\s")]
    private static partial Regex LineStartOrdered();

    [GeneratedRegex(@"^[A-Za-z0-9_]+:")]
    private static partial Regex Scheme();

    /// <summary>JavaScript's <c>/^\s*=+\s*$/</c>: a paragraph that would read as a setext heading underline.</summary>
    [GeneratedRegex(@"^[\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*=+[\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]*$")]
    private static partial Regex SetextUnderline();

    [GeneratedRegex("`+")]
    private static partial Regex BacktickRun();

    private EditorMarkdownWriter(bool projection, JsonObject document)
    {
        m_projection = projection;
        // The document being written: a chart and the table it links to refer to each other (markdownDialect.ts withMarkdownSerialization).
        m_document = document;
    }

    /// <summary>The Markdown the editor stores for <paramref name="document"/> (a <c>doc</c> node).</summary>
    public static string Write(JsonObject document)
    {
        return Write(document, document);
    }

    /// <summary>
    /// The Markdown of <paramref name="content"/> (a <c>doc</c> node holding part of <paramref name="document"/>) as it reads within the
    /// whole document: a summary counts, and a requirement names its dependencies, over <paramref name="document"/>.
    /// </summary>
    internal static string Write(JsonObject content, JsonObject document)
    {
        var writer = new EditorMarkdownWriter(false, document);
        writer.RenderContent(content);
        return writer.m_out.ToString();
    }

    /// <summary>
    /// <see cref="Write"/> with component placeholders: a <see cref="MarkdownToRich.SlotNodeType"/> node writes
    /// <c>⟦c:N⟧</c> and a literal <c>⟦</c> or <c>⟧</c> in text is escaped as <c>\⟦</c> / <c>\⟧</c>.
    /// </summary>
    public static string WriteProjection(JsonObject document)
    {
        var writer = new EditorMarkdownWriter(true, document);
        writer.RenderContent(document);
        return writer.m_out.ToString();
    }

    private static string Type(JsonNode? node)
    {
        return node?["type"]?.GetValue<string>() ?? "";
    }

    private static string? Attr(JsonNode? node, string name)
    {
        return node?["attrs"]?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static List<JsonNode> Children(JsonNode? node)
    {
        return (node?["content"] as JsonArray ?? []).Where(x => x is not null).Select(x => x!).ToList();
    }

    private static string TextOf(JsonNode node)
    {
        return node["text"]?.GetValue<string>() ?? "";
    }

    /// <summary>A text node's marks in the editor schema's order, as ProseMirror keeps them.</summary>
    private static List<JsonObject> Marks(JsonNode? node)
    {
        return (node?["marks"] as JsonArray ?? []).OfType<JsonObject>().OrderBy(RichDocument.MarkRank).ToList();
    }

    private static bool Same(JsonObject left, JsonObject right)
    {
        return JsonNode.DeepEquals(left, right);
    }

    private static bool InSet(JsonObject mark, List<JsonObject> set)
    {
        return set.Any(x => Same(x, mark));
    }

    // ---- MarkdownSerializerState --------------------------------------------------------------

    private bool AtBlank()
    {
        return m_out.Length == 0 || m_out[^1] == '\n';
    }

    private void FlushClose(int size = 2)
    {
        if (m_closed is null)
        {
            return;
        }
        if (!AtBlank())
        {
            m_out.Append('\n');
        }
        if (size > 1)
        {
            var delimMin = m_delim.TrimEnd();
            for (var i = 1; i < size; i++)
            {
                m_out.Append(delimMin).Append('\n');
            }
        }
        m_closed = null;
    }

    private void WrapBlock(string delim, string? firstDelim, JsonNode node, Action render)
    {
        var old = m_delim;
        Write(firstDelim ?? delim);
        m_delim += delim;
        render();
        m_delim = old;
        CloseBlock(node);
    }

    private void EnsureNewLine()
    {
        if (!AtBlank())
        {
            m_out.Append('\n');
        }
    }

    private void Write(string? content = null)
    {
        FlushClose();
        if (m_delim.Length > 0 && AtBlank())
        {
            m_out.Append(m_delim);
        }
        if (!string.IsNullOrEmpty(content))
        {
            m_out.Append(content);
        }
    }

    private void CloseBlock(JsonNode node)
    {
        m_closed = node;
    }

    private void Text(string text, bool escape = true)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            Write();
            // Escape an exclamation mark in front of a link.
            if (!escape && lines[i].StartsWith('[') && m_out.Length > 0 && m_out[^1] == '!' && (m_out.Length == 1 || m_out[^2] != '\\'))
            {
                m_out.Length--;
                m_out.Append("\\!");
            }
            m_out.Append(escape ? Esc(lines[i], m_atBlockStart, m_projection) : lines[i]);
            if (i != lines.Length - 1)
            {
                m_out.Append('\n');
            }
        }
    }

    private void RenderContent(JsonNode parent)
    {
        var children = Children(parent);
        for (var i = 0; i < children.Count; i++)
        {
            Render(children[i], parent, i);
        }
    }

    /// <summary>prosemirror-markdown's render plus tiptap-markdown's delimiter normalisation after each node.</summary>
    private void Render(JsonNode node, JsonNode parent, int index)
    {
        RenderNode(node, parent, index);
        if (m_inlines.Count > 0 && m_inlines[^1] is { Start: > 0, End: > 0 } top)
        {
            var start = top.Start.Value;
            while (start < m_out.Length && IsJsWhiteSpace(m_out[start]))
            {
                start++;
            }
            TrimInline(top.Delimiter ?? "", start, top.End.Value);
            m_inlines.RemoveAt(m_inlines.Count - 1);
        }
    }

    private void RenderInline(JsonNode parent, bool fromBlockStart = true)
    {
        m_atBlockStart = fromBlockStart;
        var children = Children(parent);
        var active = new List<JsonObject>();
        var trailing = "";
        for (var position = 0; position <= children.Count; position++)
        {
            Progress(position < children.Count ? children[position] : null, position);
        }
        m_atBlockStart = false;

        void Progress(JsonNode? node, int index)
        {
            var marks = node is null ? [] : Marks(node);
            // Marks that end right at a hard break close before it, so no mark closes after a new line.
            if (node is not null && Type(node) == "hardBreak")
            {
                marks = marks.Where(mark =>
                {
                    if (index + 1 == children.Count)
                    {
                        return false;
                    }
                    var next = children[index + 1];
                    return InSet(mark, Marks(next)) && (Type(next) != "text" || TextOf(next).Any(c => !IsJsWhiteSpace(c)));
                }).ToList();
            }
            var inner = marks.Count > 0 ? marks[^1] : null;
            var noEscape = inner is not null && !Spec(inner).Escape;
            var length = marks.Count - (noEscape ? 1 : 0);
            // Reorder mixable marks so their order matches the already open ones.
            for (var i = 0; i < length; i++)
            {
                var mark = marks[i];
                if (!Spec(mark).Mixable)
                {
                    break;
                }
                for (var j = 0; j < active.Count; j++)
                {
                    var other = active[j];
                    if (!Spec(other).Mixable)
                    {
                        break;
                    }
                    if (Same(mark, other))
                    {
                        if (i > j)
                        {
                            marks = [.. marks[..j], mark, .. marks[j..i], .. marks[(i + 1)..length]];
                        }
                        else if (j > i)
                        {
                            // j is a position among the open marks and may lie past this node's marks: Array.prototype.slice
                            // clamps both bounds to the array, so the mark then moves behind the rest (a list range would throw).
                            marks = [.. marks[..i], .. marks[(i + 1)..Math.Min(j, marks.Count)], mark, .. marks[Math.Min(j, length)..length]];
                        }
                        break;
                    }
                }
            }
            var keep = 0;
            while (keep < Math.Min(active.Count, length) && Same(marks[keep], active[keep]))
            {
                keep++;
            }
            var leading = trailing;
            trailing = "";
            var text = node is not null && Type(node) == "text" ? TextOf(node) : null;
            if (text is not null && marks.Any(mark => Spec(mark).Expel && !active.Take(keep).Any(open => Same(open, mark))))
            {
                var match = LeadingSpace().Match(text);
                if (match.Groups[1].Length > 0)
                {
                    leading += match.Groups[1].Value;
                    text = match.Groups[2].Length > 0 ? match.Groups[2].Value : null;
                    if (text is null)
                    {
                        node = null;
                        marks = active;
                    }
                }
            }
            if (text is not null && marks.Select((mark, i) => (mark, i)).Any(x => Spec(x.mark).Expel && !IsMarkAhead(children, index + 1, marks[..(x.i + 1)])))
            {
                var match = TrailingSpace().Match(text);
                if (match.Groups[2].Length > 0)
                {
                    trailing = match.Groups[2].Value;
                    text = match.Groups[1].Length > 0 ? match.Groups[1].Value : null;
                    if (text is null)
                    {
                        node = null;
                        marks = active;
                    }
                }
            }
            if (node is not null || index == children.Count)
            {
                while (keep < active.Count)
                {
                    var closing = active[^1];
                    active.RemoveAt(active.Count - 1);
                    Text(MarkString(closing, false, children, index), false);
                }
            }
            if (leading.Length > 0)
            {
                Text(leading);
            }
            if (node is null)
            {
                return;
            }
            while (active.Count < length)
            {
                var add = marks[active.Count];
                active.Add(add);
                Text(MarkString(add, true, children, index), false);
                m_atBlockStart = false;
            }
            if (noEscape && text is not null)
            {
                Text(MarkString(inner!, true, children, index) + text + MarkString(inner!, false, children, index + 1), false);
            }
            else
            {
                Render(text is null ? node : new JsonObject { ["type"] = "text", ["text"] = text }, parent, index);
            }
            m_atBlockStart = false;
        }
    }

    private static bool IsMarkAhead(List<JsonNode> children, int index, List<JsonObject> marks)
    {
        for (; ; index++)
        {
            if (index >= children.Count)
            {
                return false;
            }
            var next = children[index];
            if (Type(next) != "hardBreak")
            {
                var nextMarks = Marks(next);
                return nextMarks.Count >= marks.Count && nextMarks.Take(marks.Count).Zip(marks).All(x => Same(x.First, x.Second));
            }
            index++;
        }
    }

    private void RenderList(JsonNode node, string delim, Func<int, string> firstDelim)
    {
        if (m_closed is not null && Type(m_closed) == Type(node))
        {
            FlushClose(3);
        }
        else if (m_inTightList)
        {
            FlushClose(1);
        }
        // tiptap-markdown gives bullet and ordered lists a tight attribute (default true), the timed list too; a task list has none and is loose.
        var tight = node["attrs"]?["tight"] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : Type(node) is "bulletList" or "orderedList" or "timedList";
        var previous = m_inTightList;
        m_inTightList = tight;
        var items = Children(node);
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0 && tight)
            {
                FlushClose(1);
            }
            var item = items[i];
            var index = i;
            WrapBlock(delim, firstDelim(i), node, () => Render(item, node, index));
        }
        m_inTightList = previous;
    }

    private static string Esc(string text, bool startOfLine, bool placeholders = false)
    {
        var builder = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            // An underscore inside a word (ASCII word characters on both sides) stays as it is.
            if (character is '`' or '*' or '\\' or '~' or '[' or ']' or '_'
                && !(character == '_' && i > 0 && i + 1 < text.Length && IsAsciiWord(text[i - 1]) && IsAsciiWord(text[i + 1])))
            {
                builder.Append('\\');
            }
            else if (placeholders && character is MarkdownToRich.PlaceholderOpen or MarkdownToRich.PlaceholderClose)
            {
                // Outside the editor dialect: text that looks like a component placeholder stays text.
                builder.Append('\\');
            }
            builder.Append(character);
        }
        var result = builder.ToString();
        if (startOfLine)
        {
            result = LineStartMarker().Replace(result, "\\$&", 1);
            result = LineStartHeading().Replace(result, "$1\\$2$3", 1);
            result = LineStartOrdered().Replace(result, "$1\\. ", 1);
        }
        return result;
    }

    private static bool IsAsciiWord(char character)
    {
        return char.IsAsciiLetterOrDigit(character) || character == '_';
    }

    /// <summary>JavaScript's <c>\s</c>.</summary>
    private static bool IsJsWhiteSpace(char character)
    {
        return char.IsWhiteSpace(character) || character == '﻿';
    }

    // ---- marks ---------------------------------------------------------------------------------

    private readonly record struct MarkSpec(bool Mixable, bool Expel, bool Escape);

    private static MarkSpec Spec(JsonObject mark)
    {
        return Type(mark) switch
        {
            "bold" or "italic" => new MarkSpec(true, true, true),
            "link" => new MarkSpec(true, false, true),
            "strike" => new MarkSpec(false, true, true),
            "code" => new MarkSpec(false, false, false),
            // The editor dialect writes highlight as `==text==`.
            "highlight" => new MarkSpec(true, true, true),
            // Underline has no Markdown form; with HTML disabled it writes nothing.
            _ => new MarkSpec(false, false, true),
        };
    }

    private string MarkString(JsonObject mark, bool open, List<JsonNode> children, int index)
    {
        var value = Type(mark) switch
        {
            "bold" => "**",
            "italic" => "*",
            "strike" => "~~",
            "highlight" => "==",
            "code" => open ? Backticks(children[index], -1) : Backticks(children[index - 1], 1),
            "link" => open ? OpenLink(mark, children, index) : CloseLink(mark),
            _ => "",
        };
        if (Spec(mark).Expel)
        {
            if (open)
            {
                m_inlines.Add(new InlineDelimiter { Start = m_out.Length, Delimiter = value });
            }
            else
            {
                // tiptap-markdown replaces the top entry, whichever mark opened it.
                var top = m_inlines.Count > 0 ? m_inlines[^1] : null;
                if (top is not null)
                {
                    m_inlines.RemoveAt(m_inlines.Count - 1);
                }
                m_inlines.Add(new InlineDelimiter { Start = top?.Start, Delimiter = top?.Delimiter, End = m_out.Length });
            }
        }
        return value;
    }

    private string OpenLink(JsonObject mark, List<JsonNode> children, int index)
    {
        m_inAutolink = IsPlainUrl(mark, children, index);
        return m_inAutolink ? "<" : "[";
    }

    private string CloseLink(JsonObject mark)
    {
        var autolink = m_inAutolink;
        m_inAutolink = false;
        if (autolink)
        {
            return ">";
        }
        var title = Attr(mark, "title");
        return "](" + EscapeChars(Attr(mark, "href") ?? "", "()\"") + (string.IsNullOrEmpty(title) ? "" : " \"" + title.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"") + ")";
    }

    /// <summary>A link that is the only mark of a text equal to its absolute URL is written as an autolink.</summary>
    private static bool IsPlainUrl(JsonObject link, List<JsonNode> children, int index)
    {
        var href = Attr(link, "href") ?? "";
        // An address `<…>` would cut (a blank, `<` or `>` in it) is written [text](url), as the editor's link (core/kit.ts).
        if (!string.IsNullOrEmpty(Attr(link, "title")) || !Scheme().IsMatch(href) || href.Any(c => c is '<' or '>' || IsJsWhiteSpace(c)))
        {
            return false;
        }
        var content = children[index];
        var marks = Marks(content);
        if (Type(content) != "text" || TextOf(content) != href || marks.Count == 0 || !Same(marks[^1], link))
        {
            return false;
        }
        return index == children.Count - 1 || !InSet(link, Marks(children[index + 1]));
    }

    private static string Backticks(JsonNode node, int side)
    {
        var length = 0;
        if (Type(node) == "text")
        {
            var run = 0;
            foreach (var character in TextOf(node))
            {
                run = character == '`' ? run + 1 : 0;
                length = Math.Max(length, run);
            }
        }
        var result = (length > 0 && side > 0 ? " `" : "`") + new string('`', length);
        return length > 0 && side < 0 ? result + " " : result;
    }

    // ---- tiptap-markdown delimiter normalisation -------------------------------------------------

    private sealed class InlineDelimiter
    {
        public int? Start { get; init; }

        public string? Delimiter { get; init; }

        public int? End { get; init; }
    }

    /// <summary>Moves emphasis delimiters inward until CommonMark can open and close them, or drops an empty pair.</summary>
    private void TrimInline(string delim, int from, int to)
    {
        var position = from;
        while (position < to)
        {
            if (ScanDelims(position).CanOpen)
            {
                break;
            }
            ShiftDelim(delim, position, 1);
            position++;
        }
        from = position;
        position = to;
        while (position > from)
        {
            if (ScanDelims(position).CanClose)
            {
                break;
            }
            ShiftDelim(delim, position, -1);
            position--;
        }
        to = position;
        if (to - from < delim.Length + 1)
        {
            m_out.Remove(from, Math.Min(m_out.Length, to + delim.Length) - from);
        }
    }

    private void ShiftDelim(string delim, int start, int offset)
    {
        m_out.Remove(start, Math.Min(delim.Length, Math.Max(0, m_out.Length - start)));
        m_out.Insert(Math.Clamp(start + offset, 0, m_out.Length), delim);
    }

    /// <summary>markdown-it 14's <c>scanDelims(start, canSplitWord: true)</c>.</summary>
    private (bool CanOpen, bool CanClose) ScanDelims(int start)
    {
        var max = m_out.Length;
        var marker = start < max ? m_out[start] : '\0';
        int lastChar;
        if (start == 0)
        {
            lastChar = 0x20;
        }
        else if (start == 1)
        {
            lastChar = m_out[0];
            if ((lastChar & 0xF800) == 0xD800)
            {
                lastChar = 0xFFFD;
            }
        }
        else
        {
            lastChar = m_out[start - 1];
            if ((lastChar & 0xFC00) == 0xDC00)
            {
                int high = m_out[start - 2];
                lastChar = (high & 0xFC00) == 0xD800 ? 0x10000 + ((high - 0xD800) << 10) + (lastChar - 0xDC00) : 0xFFFD;
            }
            else if ((lastChar & 0xFC00) == 0xD800)
            {
                lastChar = 0xFFFD;
            }
        }
        var position = start;
        while (position < max && m_out[position] == marker)
        {
            position++;
        }
        int nextChar = position < max ? m_out[position] : 0x20;
        if ((nextChar & 0xFC00) == 0xD800)
        {
            int low = position + 1 < max ? m_out[position + 1] : 0;
            nextChar = (low & 0xFC00) == 0xDC00 ? 0x10000 + ((nextChar - 0xD800) << 10) + (low - 0xDC00) : 0xFFFD;
        }
        else if ((nextChar & 0xFC00) == 0xDC00)
        {
            nextChar = 0xFFFD;
        }
        var lastPunctuation = IsPunctuation(lastChar);
        var nextPunctuation = IsPunctuation(nextChar);
        var lastWhiteSpace = IsMarkdownWhiteSpace(lastChar);
        var nextWhiteSpace = IsMarkdownWhiteSpace(nextChar);
        var leftFlanking = !nextWhiteSpace && (!nextPunctuation || lastWhiteSpace || lastPunctuation);
        var rightFlanking = !lastWhiteSpace && (!lastPunctuation || nextWhiteSpace || nextPunctuation);
        return (leftFlanking, rightFlanking);
    }

    private static bool IsMarkdownWhiteSpace(int code)
    {
        return code is >= 0x2000 and <= 0x200A or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20 or 0xA0 or 0x1680 or 0x202F or 0x205F or 0x3000;
    }

    /// <summary>ASCII punctuation or a Unicode punctuation (P) or symbol (S) character.</summary>
    private static bool IsPunctuation(int code)
    {
        if (code < 0x80)
        {
            return code is >= 0x21 and <= 0x2F or >= 0x3A and <= 0x40 or >= 0x5B and <= 0x60 or >= 0x7B and <= 0x7E;
        }
        return code <= 0x10FFFF && CharUnicodeInfo.GetUnicodeCategory(code) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol
            or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;
    }

    // ---- nodes ---------------------------------------------------------------------------------

    private void RenderNode(JsonNode node, JsonNode parent, int index)
    {
        switch (Type(node))
        {
            case "text" when m_inAutolink:
                // Markdown reads no backslash escapes in an autolink: the address is written as it is (the editor's autolinkText, core/kit.ts).
                Text(TextOf(node), false);
                break;
            case "text":
                Text(TextOf(node).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal));
                break;
            case MarkdownToRich.PageLinkNodeType when m_projection:
                // A parsed page link by title, before the merge resolves it.
                Write("[[" + (node["attrs"]?["title"]?.GetValue<string>() ?? "") + "]]");
                break;
            case MarkdownToRich.SlotNodeType when m_projection:
                // Inline like a module's source; at the top level alone on its line, as a block.
                Text(MarkdownToRich.Placeholder(node["attrs"]?["index"]?.GetValue<int>() ?? 0), false);
                if (Type(parent) == "doc")
                {
                    CloseBlock(node);
                }
                break;
            case "paragraph" when Children(node) is [var only] && Type(only) == "text" && Marks(only).Count == 0
                && MarkdownDialect.IdParagraph().Match(TextOf(only)) is { Success: true } id && ChartTables.Contains(id.Groups[1].Value):
                // Text that reads as a chart's linked-table id ({#id} after a table) is escaped (dialect §2.5).
                Write("\\" + TextOf(only));
                CloseBlock(node);
                break;
            case "paragraph" when RequirementHeadingMarkdown.IsDependencyLine(node):
                // The dependency line (atlas-requirements 3): its label is drawn in the editor, written here.
                Write(RequirementHeadingMarkdown.DependencyLabel + " ");
                RenderInline(node);
                CloseBlock(node);
                break;
            case "paragraph":
            {
                // Editor 0.7.2 (MarkdownParagraph): a paragraph that starts with a mark writes the pending block prefix
                // first, so a later paragraph of a quote keeps its "> " instead of the mark's trimming eating it.
                if (Marks(Children(node).FirstOrDefault()).Count > 0)
                {
                    Write();
                }
                var start = m_out.Length;
                RenderInline(node);
                if (Type(parent) == "doc")
                {
                    // A note that starts like the dependency line stays a note: its colon is escaped.
                    while (start < m_out.Length && m_out[start] == '\n')
                    {
                        start++;
                    }
                    if (m_out.ToString(start, Math.Min(RequirementHeadingMarkdown.DependencyLabel.Length, m_out.Length - start)) == RequirementHeadingMarkdown.DependencyLabel)
                    {
                        m_out.Insert(start + RequirementHeadingMarkdown.DependencyLabel.Length - 1, '\\');
                    }
                }
                CloseBlock(node);
                break;
            }
            case "heading":
            {
                Write(new string('#', node["attrs"]?["level"]?.GetValue<int>() ?? 1) + " ");
                var title = m_out.Length;
                RenderInline(node, false);
                // Text ending like an attribute block stays text: its brace is escaped. A requirement writes its block (dialect §2).
                var start = m_out.Length;
                while (start > 0 && m_out[start - 1] != '\n')
                {
                    start--;
                }
                if (RequirementHeadingMarkdown.BlockStart(m_out.ToString(start, m_out.Length - start), out _) is >= 0 and var brace)
                {
                    m_out.Insert(start + brace, '\\');
                }
                // A title ending in `#` would read as the heading's closing sequence: its last run of `#` is escaped (the editor's serializeHeading).
                var run = m_out.Length;
                while (run > title && m_out[run - 1] == '#')
                {
                    run--;
                }
                if (run < m_out.Length && (run == title || m_out[run - 1] is ' ' or '\t'))
                {
                    m_out.Insert(run, '\\');
                }
                // Requirement attributes exist only on top-level headings (shape §1): a nested one is plain, as `> ## T {…}` does not read back.
                if (Type(parent) == "doc" && RequirementHeadingMarkdown.Write(node) is { } block)
                {
                    Write((m_out[^1] == ' ' ? "" : " ") + block);
                }
                CloseBlock(node);
                break;
            }
            case "blockquote":
                WrapBlock("> ", null, node, () => RenderContent(node));
                break;
            case "codeBlock":
            {
                // A language in the reserved a- namespace is written ```a-code <language> (dialect §2.5).
                var language = Attr(node, "language") ?? "";
                Write("```" + (language.StartsWith("a-", StringComparison.Ordinal) ? "a-code " + language : language) + "\n");
                Text(string.Concat(Children(node).Select(TextOf)), false);
                EnsureNewLine();
                Write("```");
                CloseBlock(node);
                break;
            }
            case "horizontalRule":
                Write("---");
                CloseBlock(node);
                break;
            case "bulletList" or "taskList" or "timedList":
                RenderList(node, "  ", _ => "- ");
                break;
            case "orderedList":
                OrderedList(node, parent, index);
                break;
            case "listItem" or "timedItem":
                RenderContent(node);
                break;
            case "taskItem":
                Write(node["attrs"]?["checked"]?.GetValue<bool>() == true ? "[x] " : "[ ] ");
                RenderContent(node);
                break;
            case "hardBreak":
                // A trailing hard break writes nothing.
                if (Children(parent).Skip(index + 1).Any(x => Type(x) != "hardBreak"))
                {
                    Write(m_inTable ? "[hardBreak]" : "\\\n");
                }
                break;
            case "image":
            {
                // The image is a block node; since editor 0.3.0 it is closed as its own block (0.2.0 glued the
                // next block onto its line).
                var title = Attr(node, "title");
                Write("![" + Esc(Attr(node, "alt") ?? "", false) + "](" + EscapeChars(Attr(node, "src") ?? "", "()")
                    + (string.IsNullOrEmpty(title) ? "" : " \"" + title.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"") + ")");
                CloseBlock(node);
                break;
            }
            case "table":
                Table(node);
                break;
            case "callout":
                WrapBlock("> ", null, node, () =>
                {
                    Write("[!" + AlertTypes.GetValueOrDefault(Attr(node, "variant") ?? "", "NOTE") + "]");
                    // A first paragraph continues the marker line, as GitHub writes it. Any other block (a list starting
                    // at 2, a fence) and a paragraph that would read as a setext underline follow an empty quote line.
                    var first = Children(node).FirstOrDefault();
                    if (first is not null && Type(first) == "paragraph" && !SetextUnderline().IsMatch(TextContent(first)))
                    {
                        EnsureNewLine();
                        // Editor 0.7.1: write the quote prefix now, so a leading mark (an alert title in bold) does not
                        // record its position before it and trim the "> " away.
                        Write();
                    }
                    else
                    {
                        CloseBlock(node);
                    }
                    RenderContent(node);
                });
                break;
            case "mathBlock":
            {
                var expression = Data(node, "expression");
                if (MathBlockSource(expression) is { } source)
                {
                    Text(source, false);
                }
                else
                {
                    Text("\\[" + expression + "\\]");
                }
                CloseBlock(node);
                break;
            }
            case "mathInline":
            {
                var expression = Data(node, "expression");
                if (MathInlineSource(expression) is { } source)
                {
                    Text(source, false);
                }
                else
                {
                    Text("\\(" + expression + "\\)");
                }
                break;
            }
            case "diagram":
            {
                var caption = Data(node, "caption");
                var source = Data(node, "source");
                // A caption has no Markdown form, so a captioned diagram keeps its plain-text form.
                if (caption.Length == 0 && !source.Contains('\r') && !source.Contains('\0'))
                {
                    Text(Fence("mermaid", source), false);
                }
                else
                {
                    Text((caption.Length > 0 ? caption + "\n" : "") + "Diagram (Mermaid):\n" + source);
                }
                CloseBlock(node);
                break;
            }
            case "tableOfContents":
            {
                var title = Data(node, "title");
                var data = node["attrs"]?["data"];
                Text((title.Length > 0 ? title : "Obsah dokumentu") + " (automaticky podle nadpisů " + data?["minLevel"] + "–" + data?["maxLevel"] + ")");
                CloseBlock(node);
                break;
            }
            case "contactCard" or "contactInline" or "chart" when DialectSource(node) is { } dialect:
                // The editor dialect's a-contact and a-chart (markdown? of the plugin), written as they are.
                Text(dialect, false);
                if (Type(node) != "contactInline")
                {
                    CloseBlock(node);
                }
                break;
            case "contactCard" or "contactInline" or "chart" or "attachment" or "openapi":
                // A module atom (the editor's defineMarkdownModule): its readable text, escaped; a block closes.
                Text(ModuleText(node));
                if (Type(node) != "contactInline")
                {
                    CloseBlock(node);
                }
                break;
            case "pageReference":
            {
                // In the dependency line the link is a section's, written as atlas://pages/… (dialect §2).
                var url = Attr(node, "url")?.Trim() ?? "";
                Write("[");
                Text(Attr(node, "title")?.Trim() ?? "");
                Write("](" + EscapeChars(RequirementHeadingMarkdown.IsDependencyLine(parent) ? RequirementHeadingMarkdown.MarkdownUrl(url) : url, "\\()") + ")");
                break;
            }
            case "youtrackIssue":
            {
                var state = Attr(node, "state")?.Trim();
                Write("[");
                Text((Attr(node, "key")?.Trim() ?? "") + ": " + (Attr(node, "title")?.Trim() ?? "") + (state is null ? "" : " · " + state));
                Write("](" + EscapeChars(WhatwgHref(Attr(node, "url") ?? ""), "\\()") + ")");
                break;
            }
            case "emoji":
            {
                var name = Attr(node, "name") ?? "";
                Write(EditorComponentText.Emoji(name) ?? ":" + name + ":");
                break;
            }
            case "secretReference":
                Write(EditorComponentText.SecretDirective(node["attrs"] as JsonObject, block: true));
                CloseBlock(node);
                break;
            case "secretInlineReference":
                Write(EditorComponentText.SecretDirective(node["attrs"] as JsonObject, block: false));
                break;
            case "concealedContent":
                RenderContent(node);
                CloseBlock(node);
                break;
            case "requirement":
                Requirement(node);
                break;
            case "overview" or "requirementSummary":
                // The requirements kind writes its readable line; any other kind the dialect's fence, which reads back (overviewNode.ts, as the editor).
                if (RequirementText.IsSummary(node))
                {
                    Write("**" + RequirementText.SummaryLabel + ":** ");
                    Text(RequirementText.Html(RequirementText.Summary(m_document)));
                    CloseBlock(node);
                }
                else if (Attr(node, "kind") is { Length: > 0 } kind && MarkdownDialect.ReadOverview("kind: " + kind) == kind)
                {
                    Text(MarkdownDialect.WriteOverview(kind), false);
                    CloseBlock(node);
                }
                break;
            default:
                // tiptap-markdown's fallback for a block without a Markdown form while HTML is disabled.
                Write("[" + Type(node) + "]");
                CloseBlock(node);
                break;
        }
    }

    /// <summary>
    /// Projector's requirement block (src/Atlas.Web/src/projector/requirementNode.ts): a task-list line "- [x] **title** — meta", the rest of the
    /// block indented and tight under it.
    /// </summary>
    private void Requirement(JsonNode node)
    {
        WrapBlock("  ", RequirementText.Checkbox(node), node, () =>
        {
            var children = Children(node);
            var titled = children.Count > 0 && Children(children[0]).Count > 0;
            if (titled)
            {
                Write("**");
                RenderInline(children[0], false);
                Write("**");
            }
            Text(RequirementText.Html((titled ? " — " : "") + RequirementText.Meta(node, m_dependencyNames ??= RequirementText.DependencyNames(m_document))));
            for (var i = 1; i < children.Count; i++)
            {
                if (i == 1)
                {
                    EnsureNewLine();
                }
                else
                {
                    FlushClose(1);
                }
                Render(children[i], node, i);
            }
        });
    }

    /// <summary>The table ids the document's charts name, read once: most documents have none, and every editor table has a chartRef.</summary>
    private HashSet<string> ChartTables => m_chartTables ??= MarkdownDialect.ChartTableIds(m_document);

    /// <summary>The dialect form of a contact or chart; null keeps the readable fallback (data it cannot carry).</summary>
    private string? DialectSource(JsonNode node)
    {
        var data = node["attrs"]?["data"] as JsonObject;
        try
        {
            return Type(node) switch
            {
                "contactCard" => MarkdownDialect.ContactsMarkdown(data),
                "contactInline" => MarkdownDialect.ContactInlineMarkdown(data),
                _ => MarkdownDialect.ChartMarkdown(data, m_document),
            };
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            // Stored data of another shape (a list where a text belongs): the editor's normalize refuses it and writes the text form.
            return null;
        }
    }

    private static string ModuleText(JsonNode node)
    {
        var data = node["attrs"]?["data"] as JsonObject;
        return Type(node) switch
        {
            "contactCard" => EditorComponentText.Contacts(data, inline: false),
            "contactInline" => EditorComponentText.Contacts(data, inline: true),
            "chart" => EditorComponentText.Chart(data),
            "attachment" => EditorComponentText.Attachment(data),
            _ => EditorComponentText.OpenApi(data),
        };
    }

    /// <summary>The WHATWG URL serialization (<c>new URL(url).href</c>) as far as .NET's absolute URI agrees with it.</summary>
    private static string WhatwgHref(string url)
    {
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? uri.AbsoluteUri : url.Trim();
    }

    private static string Data(JsonNode node, string name)
    {
        return node["attrs"]?["data"]?[name]?.GetValue<string>() ?? "";
    }

    /// <summary>ProseMirror's <c>textContent</c> of a paragraph: its text; a hard break counts as a line break.</summary>
    private static string TextContent(JsonNode paragraph)
    {
        return string.Concat(Children(paragraph).Select(child => Type(child) switch
        {
            "text" => TextOf(child),
            "hardBreak" => "\n",
            // Any other inline atom has visible text, so the paragraph is no setext underline.
            _ => "x",
        }));
    }

    /// <summary>A backtick fence longer than any backtick run in the source.</summary>
    private static string Fence(string language, string source)
    {
        var ticks = new string('`', Math.Max(3, BacktickRun().Matches(source).Select(run => run.Length + 1).DefaultIfEmpty(0).Max()));
        return ticks + language + "\n" + source + "\n" + ticks;
    }

    /// <summary>JavaScript's <c>String.prototype.trim</c> white space (also what <c>\s</c> matches).</summary>
    internal static readonly char[] JsTrimSpace =
    [
        '\t', '\n', '\v', '\f', '\r', ' ', '\u00A0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007',
        '\u2008', '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000', '\uFEFF',
    ];

    private static bool IsJsTrimSpace(char character)
    {
        return JsTrimSpace.Contains(character);
    }

    private static bool IsJsTrimmed(string value)
    {
        return value.Length == 0 || (!IsJsTrimSpace(value[0]) && !IsJsTrimSpace(value[^1]));
    }

    /// <summary>Math the editor dialect reads back unchanged: trimmed, without CR or NUL (markdown-it normalizes them).</summary>
    private static bool MathRepresentable(string expression)
    {
        return IsJsTrimmed(expression) && !expression.Contains('\r') && !expression.Contains('\0');
    }

    /// <summary>
    /// <c>$$</c> lines as written by hand; a <c>```math</c> fence for source they cannot carry (a blank or indented line,
    /// or <c>$$</c> inside); null keeps the plain-text form.
    /// </summary>
    private static string? MathBlockSource(string expression)
    {
        if (!MathRepresentable(expression))
        {
            return null;
        }
        if (expression.Split('\n').All(line => line.Length > 0 && IsJsTrimmed(line) && !line.Contains("$$", StringComparison.Ordinal)))
        {
            return "$$\n" + expression + "\n$$";
        }
        return Fence("math", expression);
    }

    /// <summary>Whether a <c>mathInline</c> node is written as <c>$…$</c> rather than its plain-text form.</summary>
    internal static bool WritesInlineMath(JsonNode node)
    {
        return MathInlineSource(Data(node, "expression")) is not null;
    }

    /// <summary><c>$…$</c> for one line without an unescaped <c>$</c> or a trailing lone backslash; null keeps the plain-text form.</summary>
    private static string? MathInlineSource(string expression)
    {
        if (!MathRepresentable(expression) || expression.Contains('\n'))
        {
            return null;
        }
        for (var index = 0; index < expression.Length; index++)
        {
            if (expression[index] == '$')
            {
                return null;
            }
            // The parser skips the character after a backslash, so a final lone backslash would escape the closing `$`.
            if (expression[index] == '\\' && ++index == expression.Length)
            {
                return null;
            }
        }
        return "$" + expression + "$";
    }

    private static string EscapeChars(string value, string characters)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (characters.Contains(character))
            {
                builder.Append('\\');
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    private void OrderedList(JsonNode node, JsonNode parent, int index)
    {
        // The editor's `node.attrs.start || 1` in JavaScript numbers: any safe integer is valid (a pasted <ol start="3000000000">),
        // and past 2^53 the item numbers round as doubles do. Within the safe range plus the item count a long holds them exactly.
        // A parsed document holds a JsonElement, a node built in code an int or long: read whichever number it is.
        var start = node["attrs"]?["start"] is JsonValue value
            ? value.TryGetValue<double>(out var real) ? real
            : value.TryGetValue<long>(out var whole) ? whole
            : value.TryGetValue<int>(out var small) ? small : 1
            : 1;
        start = start == 0 ? 1 : start;
        string Number(double value) => ((long)value).ToString(CultureInfo.InvariantCulture);
        var width = Number(start + Children(node).Count - 1).Length;
        // Adjacent ordered lists alternate the delimiter so they stay separate lists.
        var siblings = Children(parent);
        var adjacent = 0;
        while (index - adjacent > 0 && Type(siblings[index - adjacent - 1]) == Type(node))
        {
            adjacent++;
        }
        var separator = adjacent % 2 == 1 ? ") " : ". ";
        RenderList(node, new string(' ', width + 2), i =>
        {
            var number = Number(start + i);
            return new string(' ', width - number.Length) + number + separator;
        });
    }

    /// <summary>The editor's GFM table; a table whose cells are not single text blocks the dialect's ```a-table (§4.12).</summary>
    private void Table(JsonNode table)
    {
        var rows = Children(table);
        if (!MarkdownDialect.HasInlineCells(table))
        {
            Text(MarkdownDialect.TableMarkdown(table, cell =>
            {
                var writer = new EditorMarkdownWriter(m_projection, m_document);
                writer.RenderContent(cell);
                return writer.m_out.ToString();
            }), false);
            CloseBlock(table);
            return;
        }
        m_inTable = true;
        for (var index = 0; index < rows.Count; index++)
        {
            Write("| ");
            var cells = Children(rows[index]);
            for (var column = 0; column < cells.Count; column++)
            {
                if (column > 0)
                {
                    Write(" | ");
                }
                Cell(Children(cells[column])[0]);
            }
            Write(" |");
            EnsureNewLine();
            if (index == 0)
            {
                Write("| " + string.Join(" | ", cells.Select(cell => (Attr(cell, "textAlign") ?? Attr(cell, "align")) switch
                {
                    "left" => ":---",
                    "center" => ":---:",
                    "right" => "---:",
                    _ => "---",
                })) + " |");
                EnsureNewLine();
            }
        }
        CloseBlock(table);
        m_inTable = false;
        // A table a chart links to carries its id (dialect {#id} paragraph, §4.5).
        if (Attr(table, "chartRef") is { } chartRef && ChartTables.Contains(chartRef) && MarkdownDialect.ChartLinksTable(m_document, table))
        {
            Write("{#" + Attr(table, "chartRef") + "}");
            CloseBlock(table);
        }
    }

    private void Cell(JsonNode paragraph)
    {
        var content = new JsonArray();
        void Flush()
        {
            var start = m_out.Length;
            RenderInline(new JsonObject { ["type"] = Type(paragraph), ["content"] = content });
            var rendered = m_out.ToString(start, m_out.Length - start).Replace("|", "\\|", StringComparison.Ordinal);
            m_out.Length = start;
            m_out.Append(rendered);
            content = [];
        }
        foreach (var node in Children(paragraph))
        {
            if (Type(node) == "hardBreak")
            {
                Flush();
                Write("<br>");
                continue;
            }
            if (Type(node) == "text" && TextOf(node).Contains('\n'))
            {
                var parts = TextOf(node).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
                for (var i = 0; i < parts.Length; i++)
                {
                    if (i > 0)
                    {
                        Flush();
                        Write("<br>");
                    }
                    if (parts[i].Length > 0)
                    {
                        var part = node.DeepClone();
                        part["text"] = parts[i];
                        content.Add(part);
                    }
                }
                continue;
            }
            content.Add(node.DeepClone());
        }
        Flush();
    }
}
