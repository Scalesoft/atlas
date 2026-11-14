using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// The editor dialect of a requirement heading and its dependency line (plan 2026-11-04-requirement-heading-shape.md §2), read by
/// <see cref="MarkdownToRich"/> and written by <see cref="EditorMarkdownWriter"/>: a trailing attribute block on a heading line,
/// <c>### Název {#id state=waiting_for_input kind=inbound party="Jan Novák" partyContact=&lt;guid&gt; system=NIA from=&lt;pageId&gt;#&lt;id&gt; stateLabel=ověřit}</c>
/// (the party §6), and the first
/// line under it <c>Závislosti: [Titul](atlas://pages/&lt;pageId&gt;?heading=&lt;id&gt;), …</c>. A value with a space, quote, brace or
/// backslash is quoted, <c>"…"</c>, with <c>\</c> escaping <c>"</c>, <c>\</c> and the braces; a <c>{</c> after an odd number of backslashes
/// is text, so a plain heading ending in <c>{…}</c> is written with <c>\{</c>.
/// </summary>
public static partial class RequirementHeadingMarkdown
{
    /// <summary>The label of the dependency line in Markdown; in the editor it is drawn, not text.</summary>
    public const string DependencyLabel = "Závislosti:";

    /// <summary>The attribute block's keys in the order they are written, and the heading attribute each one sets.</summary>
    private static readonly (string Key, string Attribute)[] Keys =
        [("state", "state"), ("kind", "direction"), ("party", "party"), ("partyContact", "partyContactId"), ("system", "system"), ("from", "from"), ("stateLabel", "stateLabel")];

    /// <summary>The requirement attributes of a heading (atlas-requirements 4), which a heading written with <c>{#id}</c> alone keeps.</summary>
    public static readonly string[] Attributes = ["state", "stateLabel", "direction", "party", "partyContactId", "system", "from"];

    private static readonly HashSet<string> States = new(StringComparer.Ordinal) { "planned", "in_progress", "waiting_for_input", "done", "blocked", "not_applicable" };

    private static readonly HashSet<string> Directions = new(StringComparer.Ordinal) { "internal", "inbound", "outbound" };

    private const int MaxText = 200;

    [GeneratedRegex(@"\G[ \t]*(?:#(?<id>[^\s""{}\\]+)|(?<key>[A-Za-z]+)=(?:""(?<quoted>(?:[^""\\]|\\.)*)""|(?<value>[^\s""{}\\]+)))")]
    private static partial Regex Token();

    [GeneratedRegex(@"^#{1,6}(?:[ \t].*)?$")]
    private static partial Regex HeadingStart();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}#[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex From();

    [GeneratedRegex(@"[\u0000-\u001f\u007f]")]
    private static partial Regex Control();

    [GeneratedRegex(@"^[^\s""{}\\]+$")]
    private static partial Regex Bare();

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex Escaped();

    [GeneratedRegex(@"[""\\{}]")]
    private static partial Regex Quoted();

    /// <summary>
    /// Where the trailing attribute block of <paramref name="line"/> starts, or -1: the line is a heading line (<c>#</c>–<c>######</c> at
    /// column 0), the block a <c>{</c> after a space or tab and an even number of backslashes, its body made of <c>#id</c> and
    /// <c>key=value</c> tokens only, then <c>}</c> and the end of the line. Linear in the line.
    /// </summary>
    public static int BlockStart(string line, out string body)
    {
        body = "";
        var end = line.Length;
        while (end > 0 && line[end - 1] is ' ' or '\t')
        {
            end--;
        }
        if (end < 2 || line[end - 1] != '}' || OddBackslashes(line, end - 1))
        {
            return -1;
        }
        // Quoted values may hold an escaped brace only, so the block opens at the last unescaped one.
        for (var start = end - 2; start > 0; start--)
        {
            if (line[start] == '}' && !OddBackslashes(line, start))
            {
                return -1;
            }
            if (line[start] != '{' || OddBackslashes(line, start))
            {
                continue;
            }
            if (line[start - 1] is not (' ' or '\t') || !HeadingStart().IsMatch(line.AsSpan(0, start).TrimEnd(" \t")))
            {
                return -1;
            }
            body = line[(start + 1)..(end - 1)];
            return Tokens(body) is null ? -1 : start;
        }
        return -1;
    }

    private static bool OddBackslashes(string text, int index)
    {
        var count = 0;
        while (index - count > 0 && text[index - count - 1] == '\\')
        {
            count++;
        }
        return count % 2 == 1;
    }

    /// <summary>The <c>#id</c> (key <c>#</c>) and <c>key=value</c> tokens of a block body, unescaped; null when the body is anything else.</summary>
    private static List<(string Key, string Value)>? Tokens(string body)
    {
        var tokens = new List<(string, string)>();
        var at = 0;
        while (true)
        {
            while (at < body.Length && body[at] is ' ' or '\t')
            {
                at++;
            }
            if (at == body.Length)
            {
                break;
            }
            var match = Token().Match(body, at);
            // Far more tokens than keys is no block (a duplicate within reason is an error of a block).
            if (!match.Success || tokens.Count > Keys.Length * 2)
            {
                return null;
            }
            tokens.Add(match.Groups["id"].Success ? ("#", match.Groups["id"].Value)
                : (match.Groups["key"].Value, match.Groups["quoted"].Success ? Escaped().Replace(match.Groups["quoted"].Value, "$1") : match.Groups["value"].Value));
            at = match.Index + match.Length;
            // Tokens are separated by spaces.
            if (at < body.Length && body[at] is not (' ' or '\t'))
            {
                return null;
            }
        }
        return tokens.Count == 0 ? null : tokens;
    }

    /// <summary>
    /// The attributes of a block's body (from <see cref="BlockStart"/>): <c>headingId</c> and the requirement's, or the reason it is invalid.
    /// </summary>
    public static JsonObject Read(string body, out string? error)
    {
        error = null;
        var attributes = new JsonObject();
        foreach (var (key, value) in Tokens(body)!)
        {
            var name = key == "#" ? "headingId" : Keys.FirstOrDefault(x => x.Key == key).Attribute;
            if (name is null || attributes.ContainsKey(name))
            {
                error = name is null ? $"Neznámý atribut nadpisu {key} (známé jsou #id, state, kind, party, partyContact, system, from a stateLabel)." : $"Atribut nadpisu {key} je uvedený dvakrát.";
                return attributes;
            }
            error = name switch
            {
                "headingId" when !HeadingIds.IsValid(value) => "ID nadpisu tvoří písmena, číslice, tečka, podtržítko a pomlčka (nejvýše 128 znaků).",
                "state" when !States.Contains(value) => "Stav požadavku je planned, in_progress, waiting_for_input, done, blocked nebo not_applicable.",
                "direction" when !Directions.Contains(value) => "Druh požadavku (kind) je internal, inbound nebo outbound.",
                "from" when !From().IsMatch(value) => "Původ požadavku (from) je ID stránky#ID nadpisu.",
                "system" or "stateLabel" or "party" when value.Length > MaxText || Control().IsMatch(value) => $"Atribut {key} má nejvýše {MaxText} znaků na jednom řádku.",
                "partyContactId" when !Guid.TryParseExact(value, "D", out _) => "Kontakt strany (partyContact) je ID kontaktu z adresáře (GUID).",
                _ => null,
            };
            if (error is not null)
            {
                return attributes;
            }
            attributes[name] = value;
        }
        if (attributes.ContainsKey("partyContactId") && !attributes.ContainsKey("party"))
        {
            error = "Kontakt strany (partyContact) patří ke straně: uveďte i party.";
        }
        return attributes;
    }

    /// <summary>The block a heading writes after its text: <c>{#id state=…}</c> for a requirement (state not null), else null.</summary>
    public static string? Write(JsonNode heading)
    {
        var attrs = heading["attrs"] as JsonObject;
        if (Text(attrs?["state"]) is not { Length: > 0 } state)
        {
            return null;
        }
        var block = new StringBuilder("{");
        if (Text(attrs?["headingId"]) is { } id && HeadingIds.IsValid(id) && !HeadingIds.IsTemporary(id))
        {
            block.Append('#').Append(id).Append(' ');
        }
        block.Append("state=").Append(Value(state));
        foreach (var (key, attribute) in Keys.Skip(1))
        {
            if (Text(attrs?[attribute]) is { Length: > 0 } value && !(attribute == "direction" && value == "internal")
                && !(attribute == "partyContactId" && Text(attrs?["party"]) is not { Length: > 0 }))
            {
                block.Append(' ').Append(key).Append('=').Append(Value(value));
            }
        }
        return block.Append('}').ToString();
    }

    private static string Value(string value)
    {
        return Bare().IsMatch(value) ? value : "\"" + Quoted().Replace(value, "\\$0") + "\"";
    }

    private static string? Text(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>Whether a paragraph is a dependency line (<c>attrs.dependencies</c>).</summary>
    public static bool IsDependencyLine(JsonNode? paragraph)
    {
        return paragraph?["attrs"]?["dependencies"] is JsonValue value && value.TryGetValue<bool>(out var dependencies) && dependencies;
    }

    [GeneratedRegex(@"^#/pages/(?<id>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\?heading=[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex SectionHref();

    [GeneratedRegex(@"^[ \t]*,[ \t]*$")]
    private static partial Regex Separator();

    /// <summary>
    /// A parsed top-level paragraph that reads <c>Závislosti: [A](atlas://pages/…?heading=…), [B](…)</c>, the label not escaped: the
    /// dependency line, its links <c>pageReference</c> atoms separated by „, “. Null for any other paragraph (it stays a note).
    /// </summary>
    public static JsonObject? DependencyLine(JsonObject paragraph)
    {
        var content = (paragraph["content"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (content.Count < 2 || content[0]["marks"] is not null || Text(content[0]["text"]) is not { } label
            || !label.StartsWith(DependencyLabel, StringComparison.Ordinal) || label[DependencyLabel.Length..].Trim(' ', '\t').Length > 0)
        {
            return null;
        }
        var atoms = new JsonArray();
        for (var index = 1; index < content.Count; index++)
        {
            var node = content[index];
            if (index % 2 == 0)
            {
                if (node["marks"] is not null || index == content.Count - 1 || !Separator().IsMatch(Text(node["text"]) ?? ""))
                {
                    return null;
                }
                atoms.Add(new JsonObject { ["type"] = "text", ["text"] = ", " });
                continue;
            }
            var marks = node["marks"] as JsonArray;
            var href = marks is [JsonObject { } mark] && RichDocument.TypeOf(mark) == "link" ? Text(mark["attrs"]?["href"]) : null;
            var title = (Text(node["text"]) ?? "").Trim();
            if (RichDocument.TypeOf(node) != "text" || href is null || SectionHref().Match(href) is not { Success: true } page || title.Length == 0)
            {
                return null;
            }
            atoms.Add(new JsonObject
            {
                ["type"] = "pageReference",
                ["attrs"] = new JsonObject { ["id"] = page.Groups["id"].Value, ["title"] = title, ["url"] = href },
            });
        }
        return RichNodes.Node("paragraph", atoms, new JsonObject { ["dependencies"] = true });
    }

    /// <summary>A stored link of the dependency line written as Markdown writes it: <c>#/pages/…</c> as <c>atlas://pages/…</c>.</summary>
    public static string MarkdownUrl(string url)
    {
        return url.StartsWith("#/pages/", StringComparison.Ordinal) ? "atlas://pages/" + url["#/pages/".Length..] : url;
    }
}
