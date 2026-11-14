using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// The <c>a-</c> part of the editor's Markdown dialect (spec 2026-10-12 §2, §4.3, §4.5), ported from <c>@atlas/markdown</c>
/// (<c>engine/markdownDialect.ts</c>, <c>plugins/contacts/markdown.ts</c>, <c>plugins/charts/markdown.ts</c>, <c>core/tableMarkdown.ts</c>):
/// a-YAML (a strict YAML 1.2 subset, typed by the component's schema), the IRI link codec, and the two components Atlas reads and
/// writes, <c>a-contact</c> (block <c>contactCard</c>, inline <c>contactInline</c>) and <c>a-chart</c> (<c>chart</c> with its linked table),
/// and <c>a-table</c>, a table GFM cannot carry (§4.12).
/// Readers throw <see cref="DialectException"/> with the editor's Czech reason; the plugin validators then decide the data.
/// </summary>
public static partial class MarkdownDialect
{
    public const string Contact = "a-contact";
    public const string Chart = "a-chart";

    /// <summary>Atlas's overview block (<c>overview</c>, src/Atlas.Web/src/overview/overviewNode.ts): <c>```a-overview</c> with <c>kind: …</c>.</summary>
    public const string Overview = "a-overview";

    /// <summary>A table GFM cannot carry (§4.12): cells of several blocks, merged cells, unequal rows.</summary>
    public const string Table = "a-table";

    private static readonly string[] TableCellKeys = ["header", "colspan", "rowspan", "align", "content"];

    private const int MaxDepth = 4;

    // JavaScript's \s, and . (anything but a line terminator).
    private const string S = @"[\t\n\v\f\r    -     　﻿]";
    private const string Dot = @"[^\n\r  ]";

    private static readonly string[] ContactKeys =
        ["name", "role", "title", "organization", "department", "username", "phones", "emails", "website", "address", "photo", "kind", "directoryId", "objectId", "internalUser", "unresolved"];

    private static readonly string[] UserKeys = ["id", "name", "photo"];

    /// <summary>Contact fields the schema types as strings: their plain scalars read as written (<c>+420777123456</c>, <c>2026</c>).</summary>
    private static readonly string[] ContactTextKeys = ["name", "role", "title", "organization", "department", "username", "website", "address", "photo", "directoryId", "objectId"];

    /// <summary>A name in the reserved <c>a-</c> namespace.</summary>
    [GeneratedRegex("^a-[a-z][a-z0-9-]*$")]
    public static partial Regex Name();

    /// <summary>Identity written as <c>{#id}</c>: a table a chart links to.</summary>
    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    public static partial Regex Id();

    /// <summary>The one line of an <c>a-overview</c> fence (overviewNode.ts <c>readOverviewFence</c>): its kind, plain or quoted, an optional comment.</summary>
    [GeneratedRegex(@"^kind:\s*([""']?)([a-z][a-z0-9-]{0,39})\1(?:\s+#.*)?$")]
    private static partial Regex OverviewKind();

    /// <summary>The kind of an <c>a-overview</c> fence's body: one <c>kind:</c> line besides blank and comment lines; null for anything else.</summary>
    public static string? ReadOverview(string body)
    {
        var lines = body.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')).ToArray();
        return lines.Length == 1 && OverviewKind().Match(lines[0]) is { Success: true } match ? match.Groups[2].Value : null;
    }

    /// <summary>The fence of an overview of <paramref name="kind"/>, as the editor writes it (overviewNode.ts <c>overviewFenceText</c>).</summary>
    public static string WriteOverview(string kind) => "```" + Overview + "\nkind: " + kind + "\n```";

    /// <summary>A paragraph that is exactly <c>{#id}</c>.</summary>
    [GeneratedRegex(@"^\{#([^\t\n\v\f\r    -     　﻿{}]+)\}$")]
    public static partial Regex IdParagraph();

    /// <summary>An inline link target of the dialect, as the editor's link rule takes it.</summary>
    [GeneratedRegex(@"^(a-[a-z][a-z0-9-]*):([^()\t\n\v\f\r    -     　﻿]*)$")]
    public static partial Regex LinkTarget();

    [GeneratedRegex(@"^(a-[a-z][a-z0-9-]*):([^?]*)(?:\?(.*))?\z", RegexOptions.Singleline)]
    private static partial Regex LinkParts();

    [GeneratedRegex("^-(?: |$)")]
    private static partial Regex SequenceItem();

    [GeneratedRegex("^([A-Za-z_][A-Za-z0-9_.-]*) *:(?: +(" + Dot + "*))?$")]
    private static partial Regex MapEntry();

    [GeneratedRegex("^" + S + "*#")]
    private static partial Regex CommentLine();

    [GeneratedRegex(@"^ *\t")]
    private static partial Regex TabIndent();

    [GeneratedRegex("^[&*!{>%@`]")]
    private static partial Regex ScalarIndicator();

    [GeneratedRegex("^\\|([+-]?) *(?:#" + Dot + "*)?$")]
    private static partial Regex LiteralHeader();

    [GeneratedRegex("^" + S + "*(?:#" + Dot + "*)?$")]
    private static partial Regex Trailing();

    [GeneratedRegex(@"^""(?:[^""\\]|\\" + Dot + ")*\"")]
    private static partial Regex DoubleQuoted();

    [GeneratedRegex("^'(?:[^']|'')*'")]
    private static partial Regex SingleQuoted();

    [GeneratedRegex(S + "+#" + Dot + "*$")]
    private static partial Regex PlainComment();

    [GeneratedRegex(@"^[&*!{}[\]|>%@`,?]")]
    private static partial Regex PlainIndicator();

    [GeneratedRegex(":(?: |$)")]
    private static partial Regex PlainColon();

    [GeneratedRegex(@"[[\]{}]")]
    private static partial Regex FlowIndicator();

    [GeneratedRegex("^(?:null|Null|NULL|~)?$")]
    private static partial Regex YamlNull();

    [GeneratedRegex("^(?:true|True|TRUE)$")]
    private static partial Regex YamlTrue();

    [GeneratedRegex("^(?:false|False|FALSE)$")]
    private static partial Regex YamlFalse();

    [GeneratedRegex("^0o[0-7]+$")]
    private static partial Regex YamlOctal();

    [GeneratedRegex("^0x[0-9a-fA-F]+$")]
    private static partial Regex YamlHex();

    [GeneratedRegex(@"^[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?$")]
    private static partial Regex YamlNumber();

    [GeneratedRegex(@"^[-+]?\.(?:inf|Inf|INF)$|^\.(?:nan|NaN|NAN)$")]
    private static partial Regex YamlSpecial();

    [GeneratedRegex(@"^[-?:,[\]{}#&*!|>'""%@`+]")]
    private static partial Regex QuoteIndicator();

    [GeneratedRegex(@":(?: |$)| #|[\p{Cc}\p{Zl}\p{Zp}]")]
    private static partial Regex QuoteInside();

    [GeneratedRegex(@"[,[\]{}]")]
    private static partial Regex QuoteInFlow();

    [GeneratedRegex(@"[\x00-\x08\x0b-\x1f\x7f]")]
    private static partial Regex LiteralControl();

    [GeneratedRegex(@"(?:^|\n)[ \t]+(?:\n|\z)")]
    private static partial Regex BlankLiteralLine();

    [GeneratedRegex(S + "+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^([1-9]\d{0,2})-([1-9]\d{0,2})$")]
    private static partial Regex Range();

    [GeneratedRegex("^:?-+:?$")]
    private static partial Regex DelimiterCell();

    /// <summary>The editor's <c>TypeError</c> of a dialect reader: the reason a component is invalid.</summary>
    public sealed class DialectException(string message) : Exception(message);

    // ---- messages (ui/strings/input.ts, plugins/charts/strings.ts) ----

    public static string UnknownComponent(string name) => $"Neznámá komponenta {name}.";

    public static string InvalidComponent(string name, string reason) => $"Neplatná komponenta {name}: {reason}";

    public static string LinkedTableWins(string id) => $"Mřížka grafu se liší od tabulky {{#{id}}}; platí hodnoty tabulky.";

    private const string InvalidLink = "Neplatný cíl odkazu komponenty.";
    private const string InvalidShape = "Data komponenty nemají očekávaný tvar.";
    private const string YamlUnsupported = "nepodporovaný zápis (a-YAML dovoluje mapy, seznamy, krátké seznamy [a, b] a bloky |; kotvy, aliasy, tagy, flow mapy ani víc dokumentů ne).";
    private const string YamlIndentation = "neplatné odsazení.";
    private const string YamlTooDeep = "vnoření je hlubší než 4 úrovně.";
    private const string InvalidTableReference = "Neplatný odkaz na tabulku.";
    private const string InvalidMarkdownGrid = "Graf potřebuje za řádkem --- mřížku jako tabulku Markdownu (záhlaví, oddělovač, řádky).";

    private static string UnknownKey(string key) => $"Neznámý klíč {key}.";

    private static string DuplicateKey(string key) => $"duplicitní klíč {key}.";

    private static DialectException Fail(int line, string reason) => new($"Řádek {line.ToString(CultureInfo.InvariantCulture)} těla: {reason}");

    // ---- fences and links ----

    /// <summary>A backtick fence longer than any backtick run in the body (§2.2).</summary>
    public static string Fence(string info, string body)
    {
        var longest = 0;
        var run = 0;
        foreach (var character in body)
        {
            run = character == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var ticks = new string('`', Math.Max(3, longest + 1));
        return ticks + info + "\n" + body + "\n" + ticks;
    }

    /// <summary>IRI encoding (§2.3): spaces, controls, <c>%&amp;=#?()&lt;&gt;\</c>, quotes and <c>|</c> percent-encoded; anything else readable.</summary>
    private static string Encode(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (EditorMarkdownWriter.JsTrimSpace.Contains(character) || char.IsControl(character) || "%&=#?()<>\\\"'|".Contains(character))
            {
                foreach (var b in Encoding.UTF8.GetBytes(character.ToString()))
                {
                    builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
                continue;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    /// <summary>JavaScript's <c>decodeURIComponent</c>: a malformed escape or invalid UTF-8 is an error.</summary>
    private static string Decode(string value)
    {
        if (!value.Contains('%'))
        {
            return value;
        }
        var strict = new UTF8Encoding(false, true);
        var builder = new StringBuilder(value.Length);
        var bytes = new List<byte>();
        for (var index = 0; index < value.Length;)
        {
            if (value[index] != '%')
            {
                builder.Append(value[index++]);
                continue;
            }
            bytes.Clear();
            while (index < value.Length && value[index] == '%')
            {
                if (index + 2 >= value.Length || !byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
                {
                    throw new DialectException(InvalidLink);
                }
                bytes.Add(b);
                index += 3;
            }
            try
            {
                builder.Append(strict.GetString(bytes.ToArray()));
            }
            catch (DecoderFallbackException)
            {
                throw new DialectException(InvalidLink);
            }
        }
        return builder.ToString();
    }

    private static (string Name, string Path, List<(string Key, string Value)> Params) ParseLink(string target)
    {
        var match = LinkParts().Match(target);
        if (!match.Success)
        {
            throw new DialectException(InvalidLink);
        }
        var parameters = new List<(string, string)>();
        if (match.Groups[3].Success && match.Groups[3].Value.Length > 0)
        {
            foreach (var part in match.Groups[3].Value.Split('&'))
            {
                var equals = part.IndexOf('=');
                if (equals < 1)
                {
                    throw new DialectException(InvalidLink);
                }
                parameters.Add((Decode(part[..equals]), Decode(part[(equals + 1)..])));
            }
        }
        return (match.Groups[1].Value, Decode(match.Groups[2].Value), parameters);
    }

    // ---- a-YAML (§2.2) ----

    /// <summary>
    /// Parses an a-YAML body; anchors, aliases, tags, flow maps, merge keys, folded blocks and several documents are errors.
    /// Typing is schema-directed: a plain scalar at a path where <paramref name="text"/> says the component reads a string is that
    /// string exactly as written; everywhere else the YAML core schema applies.
    /// </summary>
    internal static JsonNode? ParseYaml(string source, Func<object[], bool> text)
    {
        return new YamlReader(source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'), text).Document();
    }

    /// <summary>The core schema resolution of a plain scalar. Numbers only ever fail validation, so a non-finite one is kept as 0.</summary>
    private static JsonNode? ResolvePlain(string text)
    {
        if (YamlNull().IsMatch(text))
        {
            return null;
        }
        if (YamlTrue().IsMatch(text) || YamlFalse().IsMatch(text))
        {
            return JsonValue.Create(YamlTrue().IsMatch(text));
        }
        if (YamlOctal().IsMatch(text) || YamlHex().IsMatch(text) || YamlNumber().IsMatch(text) || YamlSpecial().IsMatch(text))
        {
            return JsonValue.Create(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0);
        }
        return JsonValue.Create(text);
    }

    private static bool PlainIsString(string text)
    {
        return !(YamlNull().IsMatch(text) || YamlTrue().IsMatch(text) || YamlFalse().IsMatch(text) || YamlOctal().IsMatch(text) || YamlHex().IsMatch(text)
            || YamlNumber().IsMatch(text) || YamlSpecial().IsMatch(text));
    }

    private static string JsTrim(string value) => value.Trim(EditorMarkdownWriter.JsTrimSpace);

    private static bool JsBlank(string value) => value.AsSpan().Trim(EditorMarkdownWriter.JsTrimSpace).IsEmpty;

    private static int Indentation(string raw) => raw.Length - raw.TrimStart(EditorMarkdownWriter.JsTrimSpace).Length;

    private sealed class YamlReader(string[] lines, Func<object[], bool> text)
    {
        private int m_next;

        private readonly record struct Line(int Index, int Indent, string Text);

        /// <summary>The next line with content; blank and comment lines are skipped (outside literal blocks).</summary>
        private Line? Peek()
        {
            for (var index = m_next; index < lines.Length; index++)
            {
                var raw = lines[index];
                if (JsBlank(raw) || CommentLine().IsMatch(raw))
                {
                    continue;
                }
                if (TabIndent().IsMatch(raw))
                {
                    throw Fail(index + 1, YamlIndentation);
                }
                var indent = Indentation(raw);
                return new Line(index, indent, raw[indent..].TrimEnd(EditorMarkdownWriter.JsTrimSpace));
            }
            return null;
        }

        public JsonNode? Document()
        {
            if (Peek() is not { } first)
            {
                return null;
            }
            var value = Block(first.Indent, 1, []);
            if (Peek() is { } rest)
            {
                throw Fail(rest.Index + 1, YamlIndentation);
            }
            return value;
        }

        private JsonNode? Block(int indent, int depth, object[] path)
        {
            var line = Peek()!.Value;
            if (depth > MaxDepth)
            {
                throw Fail(line.Index + 1, YamlTooDeep);
            }
            return SequenceItem().IsMatch(line.Text) ? Sequence(indent, depth, path) : Map(indent, depth, path);
        }

        /// <summary>The value of a key or item with nothing after it: a nested block, a compact sequence at the same indent, or null.</summary>
        private JsonNode? Nested(int indent, int depth, bool sequenceAtSameIndent, object[] path)
        {
            var line = Peek();
            return line is { } next && (next.Indent > indent || (sequenceAtSameIndent && next.Indent == indent && SequenceItem().IsMatch(next.Text)))
                ? Block(next.Indent, depth, path)
                : null;
        }

        private JsonArray Sequence(int indent, int depth, object[] path)
        {
            var items = new JsonArray();
            for (var line = Peek(); line is { } current && current.Indent == indent && SequenceItem().IsMatch(current.Text); line = Peek())
            {
                m_next = current.Index + 1;
                var rest = current.Text[1..].TrimStart(EditorMarkdownWriter.JsTrimSpace);
                var column = indent + current.Text.Length - rest.Length;
                object[] item = [.. path, items.Count];
                if (rest.Length == 0)
                {
                    items.Add(Nested(indent, depth + 1, false, item));
                }
                else if (SequenceItem().IsMatch(rest))
                {
                    throw Fail(current.Index + 1, YamlUnsupported);
                }
                else if (MapEntry().IsMatch(rest) && rest[0] is not ('"' or '\''))
                {
                    if (depth + 1 > MaxDepth)
                    {
                        throw Fail(current.Index + 1, YamlTooDeep);
                    }
                    items.Add(Map(column, depth + 1, item, new Line(current.Index, column, rest)));
                }
                else
                {
                    items.Add(Scalar(rest, current.Index, indent, depth + 1, item));
                }
            }
            if (Peek() is { } after && after.Indent > indent)
            {
                throw Fail(after.Index + 1, YamlIndentation);
            }
            return items;
        }

        private JsonObject Map(int indent, int depth, object[] path, Line? first = null)
        {
            var entries = new JsonObject();
            for (var line = first ?? Peek(); line is { } current && current.Indent == indent && !SequenceItem().IsMatch(current.Text); line = Peek())
            {
                m_next = current.Index + 1;
                var match = MapEntry().Match(current.Text);
                if (!match.Success)
                {
                    throw Fail(current.Index + 1, YamlUnsupported);
                }
                var key = match.Groups[1].Value;
                if (entries.ContainsKey(key))
                {
                    throw Fail(current.Index + 1, DuplicateKey(key));
                }
                var value = match.Groups[2].Success ? JsTrim(match.Groups[2].Value) : "";
                entries[key] = value.Length > 0 ? Scalar(value, current.Index, indent, depth + 1, [.. path, key]) : Nested(indent, depth + 1, true, [.. path, key]);
            }
            if (Peek() is { } after && after.Indent > indent)
            {
                throw Fail(after.Index + 1, YamlIndentation);
            }
            return entries;
        }

        private JsonNode? Scalar(string value, int index, int indent, int depth, object[] path)
        {
            var line = index + 1;
            if (ScalarIndicator().IsMatch(value))
            {
                throw Fail(line, YamlUnsupported);
            }
            if (LiteralHeader().Match(value) is { Success: true } literal)
            {
                return JsonValue.Create(Literal(indent, literal.Groups[1].Value));
            }
            if (value.StartsWith('['))
            {
                if (depth > MaxDepth)
                {
                    throw Fail(line, YamlTooDeep);
                }
                var close = value.LastIndexOf(']');
                if (close < 0 || !Trailing().IsMatch(value[(close + 1)..]))
                {
                    throw Fail(line, YamlUnsupported);
                }
                return Flow(value[1..close], line, path);
            }
            return Single(value, line, false, path);
        }

        /// <summary>A one-line scalar: double-quoted (JSON escapes), single-quoted, or plain.</summary>
        private JsonNode? Single(string value, int line, bool flow, object[] path)
        {
            if (value.StartsWith('"'))
            {
                var end = DoubleQuoted().Match(value);
                if (!end.Success || !Trailing().IsMatch(value[end.Length..]))
                {
                    throw Fail(line, YamlUnsupported);
                }
                try
                {
                    return JsonValue.Create(JsonSerializer.Deserialize<string>(end.Value));
                }
                catch (JsonException)
                {
                    throw Fail(line, YamlUnsupported);
                }
            }
            if (value.StartsWith('\''))
            {
                var end = SingleQuoted().Match(value);
                if (!end.Success || !Trailing().IsMatch(value[end.Length..]))
                {
                    throw Fail(line, YamlUnsupported);
                }
                return JsonValue.Create(end.Value[1..^1].Replace("''", "'", StringComparison.Ordinal));
            }
            var plain = JsTrim(PlainComment().Replace(value, "", 1));
            if (PlainIndicator().IsMatch(plain) || PlainColon().IsMatch(plain) || (flow && FlowIndicator().IsMatch(plain)))
            {
                throw Fail(line, YamlUnsupported);
            }
            return text(path) ? JsonValue.Create(plain) : ResolvePlain(plain);
        }

        /// <summary>A short flow sequence of scalars: <c>[a, "b, c"]</c>.</summary>
        private JsonArray Flow(string inner, int line, object[] path)
        {
            var items = new List<string>();
            var current = new StringBuilder();
            var quote = '\0';
            for (var index = 0; index < inner.Length; index++)
            {
                var character = inner[index];
                if (quote != '\0')
                {
                    current.Append(character);
                    if (quote == '"' && character == '\\')
                    {
                        if (++index < inner.Length)
                        {
                            current.Append(inner[index]);
                        }
                    }
                    else if (character == quote)
                    {
                        quote = '\0';
                    }
                    continue;
                }
                if (character is '"' or '\'')
                {
                    quote = character;
                }
                if (character == ',')
                {
                    items.Add(current.ToString());
                    current.Clear();
                    continue;
                }
                current.Append(character);
            }
            if (quote != '\0')
            {
                throw Fail(line, YamlUnsupported);
            }
            if (!JsBlank(current.ToString()) || items.Count > 0)
            {
                items.Add(current.ToString());
            }
            var result = new JsonArray();
            for (var index = 0; index < items.Count; index++)
            {
                if (JsBlank(items[index]))
                {
                    throw Fail(line, YamlUnsupported);
                }
                result.Add(Single(JsTrim(items[index]), line, true, [.. path, index]));
            }
            return result;
        }

        /// <summary><c>|</c>, <c>|-</c>, <c>|+</c>: lines indented past the key, blank lines included.</summary>
        private string Literal(int indent, string chomp)
        {
            var content = new List<string>();
            var blockIndent = -1;
            while (m_next < lines.Length)
            {
                var raw = lines[m_next];
                if (JsBlank(raw))
                {
                    content.Add("");
                    m_next++;
                    continue;
                }
                var own = Indentation(raw);
                if (blockIndent < 0)
                {
                    if (own <= indent)
                    {
                        break;
                    }
                    if (TabIndent().IsMatch(raw))
                    {
                        throw Fail(m_next + 1, YamlIndentation);
                    }
                    blockIndent = own;
                }
                if (own < blockIndent)
                {
                    break;
                }
                content.Add(raw[blockIndent..]);
                m_next++;
            }
            var trailing = 0;
            while (content.Count > 0 && content[^1].Length == 0)
            {
                content.RemoveAt(content.Count - 1);
                trailing++;
            }
            // Blank lines after the block belong to the next construct, not to this scalar.
            if (chomp != "+")
            {
                m_next -= trailing;
            }
            var joined = string.Join('\n', content);
            return chomp switch
            {
                "-" => joined,
                "+" => joined + (content.Count > 0 ? "\n" : "") + new string('\n', trailing),
                _ => content.Count > 0 ? joined + "\n" : "",
            };
        }
    }

    /// <summary>a-YAML for a map or sequence, keys in their order; no trailing newline.</summary>
    internal static string WriteYaml(JsonNode value)
    {
        return string.Join('\n', WriteNode(value, 0));
    }

    private static bool IsScalar(JsonNode? value) => value is null or JsonValue;

    private static string? StringOf(JsonNode? value) => value is JsonValue item && item.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A string reads back as itself when written plain; otherwise it gets JSON double quotes. A leading <c>+</c> is quoted too.</summary>
    private static string ScalarText(JsonNode? value, bool flow)
    {
        if (StringOf(value) is not { } text)
        {
            return value is null ? "null" : value.ToJsonString();
        }
        var plain = text.Length > 0 && PlainIsString(text) && JsTrim(text) == text && !QuoteIndicator().IsMatch(text) && !QuoteInside().IsMatch(text)
            && !(flow && QuoteInFlow().IsMatch(text));
        return plain ? text : JsString(text);
    }

    /// <summary>JavaScript's <c>JSON.stringify</c> of a string: only quotes, backslashes, controls and lone surrogates are escaped.</summary>
    private static string JsString(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    var paired = char.IsHighSurrogate(character) ? index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])
                        : !char.IsLowSurrogate(character) || (index > 0 && char.IsHighSurrogate(value[index - 1]));
                    if (character < 0x20 || !paired)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    private static bool LiteralSafe(string value)
    {
        return value.Contains('\n') && !LiteralControl().IsMatch(value) && value[0] is not (' ' or '\n') && !BlankLiteralLine().IsMatch(value);
    }

    private static IEnumerable<string> LiteralLines(string value, string pad)
    {
        var trailing = value.Length - value.TrimEnd('\n').Length;
        var header = trailing switch { 0 => "|-", 1 => "|", _ => "|+" };
        var body = trailing == 0 ? value : value[..^1];
        return [header, .. body.Split('\n').Select(line => line.Length > 0 ? pad + line : "")];
    }

    private static List<string> WriteEntry(string prefix, JsonNode? value, int indent)
    {
        var pad = new string(' ', indent + 2);
        if (StringOf(value) is { } text && LiteralSafe(text))
        {
            var lines = LiteralLines(text, pad).ToList();
            return [prefix + " " + lines[0], .. lines.Skip(1)];
        }
        if (IsScalar(value))
        {
            return [prefix + " " + ScalarText(value, false)];
        }
        if (value is JsonArray array && array.All(item => IsScalar(item) && StringOf(item)?.Contains('\n') != true))
        {
            return [prefix + " [" + string.Join(", ", array.Select(item => ScalarText(item, true))) + "]"];
        }
        return [prefix, .. WriteNode(value, indent + 2)];
    }

    private static List<string> WriteNode(JsonNode? value, int indent)
    {
        var pad = new string(' ', indent);
        switch (value)
        {
            case JsonArray array:
                return array.SelectMany(item =>
                {
                    if (item is JsonObject)
                    {
                        var lines = WriteNode(item, indent + 2);
                        return lines.Count > 0 ? [pad + "- " + lines[0][(indent + 2)..], .. lines.Skip(1)] : new List<string> { pad + "- null" };
                    }
                    return WriteEntry(pad + "-", item, indent);
                }).ToList();
            case JsonObject map:
                return map.SelectMany(entry => WriteEntry(pad + entry.Key + ":", entry.Value, indent)).ToList();
            default:
                return [pad + ScalarText(value, false)];
        }
    }

    // ---- a-contact (§4.3) ----

    private static bool IsContactText(object[] path)
    {
        return path is [int, string key] ? ContactTextKeys.Contains(key)
            : path is [int, "phones" or "emails", int] || path is [int, "internalUser", string field] && UserKeys.Contains(field);
    }

    /// <summary>Keys in writing order; null and absent values are left out.</summary>
    private static JsonObject Ordered(JsonObject value, string[] keys)
    {
        var result = new JsonObject();
        foreach (var key in keys)
        {
            if (value[key] is { } item)
            {
                result[key] = item.DeepClone();
            }
        }
        return result;
    }

    /// <summary>The object's entries without nulls; not an object, or a key outside <paramref name="keys"/>, is an error.</summary>
    private static JsonObject Known(JsonNode? value, string[] keys)
    {
        if (value is not JsonObject map)
        {
            throw new DialectException(InvalidShape);
        }
        if (map.FirstOrDefault(x => !keys.Contains(x.Key)) is { Key: not null } unknown)
        {
            throw new DialectException(UnknownKey(unknown.Key));
        }
        var result = new JsonObject();
        foreach (var (key, item) in map)
        {
            if (item is not null)
            {
                result[key] = item.DeepClone();
            }
        }
        return result;
    }

    /// <summary>The ```a-contact fence of contact data; null when the data is not a contact list.</summary>
    public static string? ContactsMarkdown(JsonObject? data)
    {
        if (data?["contacts"] is not JsonArray { Count: > 0 } contacts || contacts.Any(x => x is not JsonObject))
        {
            return null;
        }
        var list = new JsonArray(Normalized(contacts).Select(contact =>
        {
            var ordered = Ordered(contact, ContactKeys);
            if (ordered["internalUser"] is JsonObject user)
            {
                ordered["internalUser"] = Ordered(user, UserKeys);
            }
            return (JsonNode)ordered;
        }).ToArray());
        return Fence(Contact, WriteYaml(list));
    }

    /// <summary>The inline <c>[Jméno](a-contact:?name=…&amp;phone=…)</c> of one contact; null when the data is not one contact.</summary>
    public static string? ContactInlineMarkdown(JsonObject? data)
    {
        if (data?["contacts"] is not JsonArray { Count: 1 } contacts || contacts[0] is not JsonObject)
        {
            return null;
        }
        var contact = Normalized(contacts)[0];
        var parameters = new List<string>();
        foreach (var key in ContactKeys)
        {
            switch (contact[key])
            {
                case null:
                    break;
                case JsonArray items:
                    var singular = key == "phones" ? "phone" : "email";
                    parameters.AddRange(items.Select(item => Encode(singular) + "=" + Encode(StringOf(item) ?? "")));
                    break;
                case JsonObject user:
                    parameters.AddRange(UserKeys.Where(field => user[field] is not null)
                        .Select(field => Encode("internalUser." + field) + "=" + Encode(StringOf(user[field]) ?? "")));
                    break;
                default:
                    parameters.Add(Encode(key) + "=" + Encode(StringOf(contact[key]) ?? contact[key]!.ToJsonString()));
                    break;
            }
        }
        var label = Spaces().Replace(StringOf(contact["name"]) ?? "", " ");
        label = Regex.Replace(label, @"[\\\[\]]", @"\$&");
        return "[" + label + "](" + Contact + ":" + (parameters.Count > 0 ? "?" + string.Join('&', parameters) : "") + ")";
    }

    /// <summary>The contacts of a ```a-contact body, before validation.</summary>
    public static JsonObject ReadContacts(string body)
    {
        if (ParseYaml(body, IsContactText) is not JsonArray contacts)
        {
            throw new DialectException(InvalidShape);
        }
        var list = new JsonArray();
        foreach (var item in contacts)
        {
            var contact = Known(item, ContactKeys);
            if (contact.ContainsKey("internalUser"))
            {
                contact["internalUser"] = Known(contact["internalUser"], UserKeys);
            }
            list.Add(contact);
        }
        return new JsonObject { ["version"] = 1, ["contacts"] = list };
    }

    /// <summary>The contact of an inline <c>a-contact:</c> target, before validation; the label is not read.</summary>
    public static JsonObject ReadContactLink(string target)
    {
        var link = ParseLink(target);
        if (link.Name != Contact || link.Path.Length > 0)
        {
            throw new DialectException(InvalidLink);
        }
        var contact = new JsonObject();
        var user = new JsonObject();
        foreach (var (key, value) in link.Params)
        {
            if (key is "phone" or "email")
            {
                var list = key + "s";
                if (contact[list] is not JsonArray items)
                {
                    contact[list] = items = [];
                }
                items.Add(value);
                continue;
            }
            var field = key.StartsWith("internalUser.", StringComparison.Ordinal) ? key["internalUser.".Length..] : null;
            var allowed = field is not null ? UserKeys.Contains(field) : ContactKeys.Contains(key) && key is not ("internalUser" or "phones" or "emails");
            if (!allowed)
            {
                throw new DialectException(UnknownKey(key));
            }
            var owner = field is null ? contact : user;
            if (owner.ContainsKey(field ?? key))
            {
                throw new DialectException(DuplicateKey(key));
            }
            owner[field ?? key] = value;
        }
        if (user.Count > 0)
        {
            contact["internalUser"] = user;
        }
        return new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(contact) };
    }

    /// <summary>
    /// plugins/contacts/data.ts normalizeContact on data its validator accepted: text trimmed, empty optional fields left out,
    /// phones and e-mails trimmed and deduplicated, in the editor's key order.
    /// </summary>
    public static JsonObject NormalizeContacts(JsonObject data)
    {
        return new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(Normalized((JsonArray)data["contacts"]!).Select(x => (JsonNode)x).ToArray()) };
    }

    private static List<JsonObject> Normalized(JsonArray contacts)
    {
        return contacts.OfType<JsonObject>().Select(contact =>
        {
            string? Text(JsonNode? value) => StringOf(value) is { } text && JsTrim(text) is { Length: > 0 } trimmed ? trimmed : null;
            var result = new JsonObject { ["name"] = Text(contact["name"]) ?? "" };
            if (contact["kind"] is { } kind)
            {
                result["kind"] = kind.DeepClone();
            }
            if (contact["internalUser"] is JsonObject internalUser)
            {
                var user = new JsonObject { ["id"] = JsTrim(StringOf(internalUser["id"]) ?? "") };
                foreach (var field in new[] { "name", "photo" })
                {
                    if (Text(internalUser[field]) is { } value)
                    {
                        user[field] = value;
                    }
                }
                result["internalUser"] = user;
            }
            foreach (var key in new[] { "organization", "department", "title", "address", "directoryId", "role", "username", "photo", "website" })
            {
                if (Text(contact[key]) is { } value)
                {
                    result[key] = value;
                }
            }
            // An invalid id is dropped, like the editor does.
            if (Text(contact["objectId"]) is { } objectId && Guid.TryParse(objectId, out var guid))
            {
                result["objectId"] = guid.ToString();
            }
            // Several people of this name, nobody picked yet (plugins/contacts/data.ts): `unresolved=true` inline is text.
            else if (contact["unresolved"] is JsonValue unresolved && (unresolved.TryGetValue<bool>(out var flag) ? flag : StringOf(unresolved) == "true"))
            {
                result["unresolved"] = true;
            }
            foreach (var key in new[] { "phones", "emails" })
            {
                if (contact[key] is JsonArray values)
                {
                    result[key] = new JsonArray(values.Select(x => JsTrim(StringOf(x) ?? "")).Distinct(StringComparer.Ordinal).Select(x => (JsonNode)x).ToArray());
                }
            }
            return result;
        }).ToList();
    }

    // ---- a-chart (§4.5) ----

    private static bool IsChartHeaderText(object[] path) => path is ["title"] or ["source", "table"];

    private static string EscapeCell(string cell)
    {
        return cell.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// The ```a-chart fence of a chart; null keeps the readable fallback: a grid cell holds <c>\r</c>, or the chart links a table the
    /// dialect cannot name (<see cref="SourceRectangle"/>).
    /// </summary>
    public static string? ChartMarkdown(JsonObject? data, JsonObject document)
    {
        if (data?["grid"] is not JsonArray { Count: > 0 } rows || rows.Any(x => x is not JsonArray row || row.Any(cell => StringOf(cell) is null)))
        {
            return null;
        }
        var grid = rows.Select(x => ((JsonArray)x!).Select(cell => StringOf(cell)!).ToList()).ToList();
        if (grid.Any(row => row.Any(cell => cell.Contains('\r'))))
        {
            return null;
        }
        JsonObject? source = null;
        if (data["source"] is { } linked)
        {
            if (linked is not JsonObject link || SourceRectangle(document, link) is not { } rectangle)
            {
                return null;
            }
            source = new JsonObject { ["table"] = rectangle.Table, ["rows"] = rectangle.Rows, ["columns"] = rectangle.Columns };
        }
        var header = new JsonObject();
        if (StringOf(data["title"]) is { Length: > 0 } title)
        {
            header["title"] = title;
        }
        if (StringOf(data["kind"]) is { } kind && kind != "bar")
        {
            header["kind"] = kind;
        }
        if (source is not null)
        {
            header["source"] = source;
        }
        string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Select(EscapeCell)) + " |";
        var lines = new List<string>();
        if (header.Count > 0)
        {
            lines.Add(WriteYaml(header));
        }
        lines.Add("---");
        lines.Add(Row(grid[0]));
        lines.Add("| " + string.Join(" | ", grid[0].Select(_ => "---")) + " |");
        lines.AddRange(grid.Skip(1).Select(Row));
        return Fence(Chart, string.Join('\n', lines));
    }

    /// <summary>Whether a table is written as a GFM table: one text block per cell, no spans, equal rows.</summary>
    internal static bool HasInlineCells(JsonNode table)
    {
        var rows = Content(table);
        var width = rows.Count == 0 ? 0 : Content(rows[0]).Count;
        return rows.All(row => Content(row).Count == width && Content(row).All(cell =>
        {
            var content = Content(cell);
            return Span(cell, "colspan") <= 1 && Span(cell, "rowspan") <= 1 && content.Count == 1 && RichDocument.TypeOf(content[0]) is "paragraph" or "heading" or "codeBlock";
        }));
    }

    private static int Span(JsonNode cell, string name)
    {
        return cell["attrs"]?[name] is JsonValue value && value.TryGetValue<int>(out var span) ? span : 1;
    }

    // ---- a-table (§4.12) ----

    /// <summary>
    /// The ```a-table fence of a table that is not written as a GFM table (<see cref="HasInlineCells"/>): a row per item, each cell with
    /// its header flag, spans, alignment and its blocks as Markdown (<paramref name="cellMarkdown"/>). tableMarkdown.ts writes the same.
    /// </summary>
    public static string TableMarkdown(JsonNode table, Func<JsonNode, string> cellMarkdown)
    {
        var rows = Content(table).Select(row => (JsonNode)new JsonObject
        {
            ["cells"] = new JsonArray(Content(row).Select(cell =>
            {
                var item = new JsonObject();
                if (RichDocument.TypeOf(cell) == "tableHeader")
                {
                    item["header"] = true;
                }
                foreach (var span in new[] { "colspan", "rowspan" })
                {
                    if (Span(cell, span) > 1)
                    {
                        item[span] = Span(cell, span);
                    }
                }
                // cellAlignment: textAlign || align.
                if ((StringOf(cell["attrs"]?["textAlign"]) is { Length: > 0 } align ? align : StringOf(cell["attrs"]?["align"])) is { Length: > 0 } alignment)
                {
                    item["align"] = alignment;
                }
                item["content"] = cellMarkdown(cell);
                return (JsonNode)item;
            }).ToArray()),
        });
        return Fence(Table, WriteYaml(new JsonArray(rows.ToArray())));
    }

    /// <summary>The rows of a ```a-table body: each cell's node type, attributes and Markdown, before the Markdown is read.</summary>
    public static List<List<(string Type, JsonObject? Attributes, string Markdown)>> ReadTable(string body)
    {
        if (ParseYaml(body, path => path is [.., "content"]) is not JsonArray { Count: > 0 } rows)
        {
            throw new DialectException(InvalidShape);
        }
        return rows.Select(row => Known(row, ["cells"])["cells"] is JsonArray cells ? cells.Select(TableCell).ToList() : throw new DialectException(InvalidShape)).ToList();
    }

    private static (string, JsonObject?, string) TableCell(JsonNode? item)
    {
        var cell = Known(item, TableCellKeys);
        var attributes = new JsonObject();
        foreach (var span in new[] { "colspan", "rowspan" })
        {
            if (cell[span] is not { } value)
            {
                continue;
            }
            // The validator bounds a table to 1000 cells, spans included.
            if (value is not JsonValue number || !number.TryGetValue<double>(out var count) || count != Math.Floor(count) || count is < 1 or > 1000)
            {
                throw new DialectException(InvalidShape);
            }
            attributes[span] = (int)count;
        }
        if (cell["align"] is { } align)
        {
            if (StringOf(align) is not ("left" or "center" or "right"))
            {
                throw new DialectException(InvalidShape);
            }
            attributes["textAlign"] = StringOf(align);
            attributes["align"] = StringOf(align);
        }
        if ((cell["header"] is { } header && (header is not JsonValue flag || !flag.TryGetValue<bool>(out _))) || (cell["content"] is { } content && StringOf(content) is null))
        {
            throw new DialectException(InvalidShape);
        }
        var type = cell["header"]?.GetValue<bool>() == true ? "tableHeader" : "tableCell";
        return (type, attributes.Count > 0 ? attributes : null, StringOf(cell["content"]) ?? "");
    }

    private static List<JsonNode> Content(JsonNode? node) => (node?["content"] as JsonArray ?? []).Where(x => x is not null).Select(x => x!).ToList();

    private static IEnumerable<JsonObject> Descendants(JsonNode? node)
    {
        foreach (var child in Content(node).OfType<JsonObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static string? ChartRef(JsonNode? node) => StringOf(node?["attrs"]?["chartRef"]);

    private static JsonObject? TableById(JsonObject document, string id)
    {
        return Descendants(document).FirstOrDefault(x => RichDocument.TypeOf(x) == "table" && ChartRef(x) == id);
    }

    /// <summary>
    /// The dialect's <c>source</c> of a chart: the linked table's <c>{#id}</c> and the 1-based rows and columns (header row included)
    /// its cells cover. Null when the table is missing, is no GFM table, its id is no dialect id or the cells are not one rectangle of it.
    /// </summary>
    private static (string Table, string Rows, string Columns)? SourceRectangle(JsonObject document, JsonObject source)
    {
        if (StringOf(source["tableId"]) is not { } id || !Id().IsMatch(id) || TableById(document, id) is not { } table || !HasInlineCells(table)
            || source["cells"] is not JsonArray { Count: > 0 } cells || cells[0] is not JsonArray { Count: > 0 } first)
        {
            return null;
        }
        var at = new Dictionary<string, (int Row, int Column)>(StringComparer.Ordinal);
        var rows = Content(table);
        for (var r = 0; r < rows.Count; r++)
        {
            var row = Content(rows[r]);
            for (var c = 0; c < row.Count; c++)
            {
                if (ChartRef(row[c]) is { Length: > 0 } cell)
                {
                    at[cell] = (r, c);
                }
            }
        }
        if (StringOf(first[0]) is not { } corner || !at.TryGetValue(corner, out var origin))
        {
            return null;
        }
        for (var r = 0; r < cells.Count; r++)
        {
            if (cells[r] is not JsonArray row)
            {
                return null;
            }
            for (var c = 0; c < row.Count; c++)
            {
                if (StringOf(row[c]) is not { } cell || !at.TryGetValue(cell, out var place) || place != (origin.Row + r, origin.Column + c))
                {
                    return null;
                }
            }
        }
        return (id, $"{origin.Row + 1}-{origin.Row + cells.Count}", $"{origin.Column + 1}-{origin.Column + first.Count}");
    }

    /// <summary>Whether <paramref name="table"/> is the table a chart of <paramref name="document"/> links with a dialect <c>source</c>.</summary>
    public static bool ChartLinksTable(JsonObject document, JsonNode table)
    {
        if (ChartRef(table) is not { } id || !ReferenceEquals(TableById(document, id), table))
        {
            return false;
        }
        return Descendants(document).Any(x => RichDocument.TypeOf(x) == "chart" && x["attrs"]?["data"]?["source"] is JsonObject source
            && StringOf(source["tableId"]) == id && source["cells"] is JsonArray && SourceRectangle(document, source) is not null);
    }

    /// <summary>The table ids the charts of <paramref name="document"/> name (a paragraph <c>{#id}</c> of one is escaped).</summary>
    public static HashSet<string> ChartTableIds(JsonObject document)
    {
        return Descendants(document).Where(x => RichDocument.TypeOf(x) == "chart").Select(x => x["attrs"]?["data"]?["source"] is JsonObject source ? StringOf(source["tableId"]) : null)
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The table id a ```a-chart body names in its header, if its header reads; the linked table's <c>{#id}</c> paragraph.</summary>
    public static string? ChartTableId(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var separator = Array.IndexOf(lines, "---");
        try
        {
            var header = separator < 0 ? null : ParseYaml(string.Join('\n', lines[..separator]), IsChartHeaderText);
            return StringOf(header?["source"]?["table"]);
        }
        catch (Exception error) when (error is DialectException or InvalidOperationException)
        {
            // The chart's own reading reports it (a header that is no map indexes as such).
            return null;
        }
    }

    /// <summary>Cells of a GFM table row; <c>\|</c>, <c>\\</c> and <c>\n</c> are escapes, cells are trimmed (N11).</summary>
    private static List<string> RowCells(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var closed = false;
        var text = JsTrim(line);
        for (var index = text.StartsWith('|') ? 1 : 0; index < text.Length; index++)
        {
            var character = text[index];
            closed = false;
            if (character == '\\' && index + 1 < text.Length)
            {
                current.Append(character).Append(text[++index]);
                continue;
            }
            if (character == '|')
            {
                cells.Add(current.ToString());
                current.Clear();
                closed = true;
                continue;
            }
            current.Append(character);
        }
        if (!closed)
        {
            cells.Add(current.ToString());
        }
        return cells.Select(cell => Regex.Replace(JsTrim(cell), @"\\([\\|n])", match => match.Groups[1].Value == "n" ? "\n" : match.Groups[1].Value)).ToList();
    }

    private static List<List<string>> ReadGrid(List<string> lines)
    {
        while (lines.Count > 0 && JsBlank(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }
        // A blank header or delimiter line is no grid (the editor tests the lines for truth).
        if (lines.Count < 2 || lines[0].Length == 0 || lines[1].Length == 0 || RowCells(lines[1]) is var delimiter && (!delimiter.All(DelimiterCell().IsMatch) || delimiter.Count != RowCells(lines[0]).Count))
        {
            throw new DialectException(InvalidMarkdownGrid);
        }
        return lines.Where((_, index) => index != 1).Select(RowCells).ToList();
    }

    private static (int From, int To) ChartRange(JsonNode? value)
    {
        var match = StringOf(value) is { } text ? Range().Match(text) : Match.Empty;
        if (!match.Success || int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) < int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
        {
            throw new DialectException(InvalidTableReference);
        }
        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) - 1);
    }

    /// <summary>
    /// The chart of a ```a-chart body, before validation. A linked table (<paramref name="tableById"/>: the table node read with that
    /// <c>{#id}</c>, its cells carrying their new ids) gives the values; a grid that differs is <paramref name="warn"/>ed, the table wins.
    /// </summary>
    public static JsonObject ReadChart(string body, Func<string, JsonObject?> tableById, Action<string> warn)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        var separator = lines.IndexOf("---");
        if (separator < 0)
        {
            throw new DialectException(InvalidMarkdownGrid);
        }
        var parsed = ParseYaml(string.Join('\n', lines.Take(separator)), IsChartHeaderText);
        var header = parsed is null ? new JsonObject() : Known(parsed, ["title", "kind", "source"]);
        var grid = ReadGrid(lines.Skip(separator + 1).ToList());
        var data = new JsonObject
        {
            ["version"] = 1, ["title"] = header["title"]?.DeepClone() ?? "", ["kind"] = header["kind"]?.DeepClone() ?? "bar",
        };
        if (header["source"] is not { } link)
        {
            data["grid"] = Grid(grid);
            data["source"] = null;
            data["sourceStatus"] = null;
            return data;
        }
        var source = Known(link, ["table", "rows", "columns"]);
        if (StringOf(source["table"]) is not { } id)
        {
            throw new DialectException(InvalidTableReference);
        }
        var rows = ChartRange(source["rows"]);
        var columns = ChartRange(source["columns"]);
        var table = tableById(id) ?? throw new DialectException($"Tabulka {{#{id}}} pro graf neexistuje.");
        var tableRows = Content(table);
        var cells = new List<List<string>>();
        var values = new List<List<string>>();
        for (var r = rows.From; r <= rows.To; r++)
        {
            var row = r < tableRows.Count ? Content(tableRows[r]) : [];
            var ids = new List<string>();
            var texts = new List<string>();
            for (var c = columns.From; c <= columns.To; c++)
            {
                if (c >= row.Count || ChartRef(row[c]) is not { Length: > 0 } cell)
                {
                    throw new DialectException(InvalidTableReference);
                }
                ids.Add(cell);
                texts.Add(TextContent(row[c]));
            }
            cells.Add(ids);
            values.Add(texts);
        }
        if (JsonSerializer.Serialize(values) != JsonSerializer.Serialize(grid))
        {
            warn(LinkedTableWins(id));
        }
        data["grid"] = Grid(values);
        data["source"] = new JsonObject { ["tableId"] = id, ["cells"] = Grid(cells) };
        data["sourceStatus"] = "linked";
        return data;
    }

    private static JsonArray Grid(List<List<string>> rows)
    {
        return new JsonArray(rows.Select(row => (JsonNode)new JsonArray(row.Select(cell => (JsonNode)JsonValue.Create(cell)).ToArray())).ToArray());
    }

    /// <summary>
    /// The text of a cell as the editor reads it from the parsed table. shortcut: text nodes only (an inline formula or contact counts
    /// as nothing, where the editor's DOM counts its literal); upgrade when a chart reads a cell holding one.
    /// </summary>
    private static string TextContent(JsonNode node)
    {
        return RichDocument.TypeOf(node) == "text" ? StringOf(node["text"]) ?? "" : string.Concat(Content(node).Select(TextContent));
    }
}
