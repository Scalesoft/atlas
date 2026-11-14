using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// What the editor's component plugins (<c>@atlas/markdown</c> 0.9.1) write into Markdown, for
/// <see cref="EditorMarkdownWriter"/>: the readable text of a module (contacts, chart, attachment, OpenAPI summary), the
/// emoji character of a shortcode and the password directive. Each mirrors the plugin's normalization as far as it changes
/// the text (trimmed fields, deduplicated phones). The golden vitest
/// (<c>src/wikiImportGolden.test.ts</c>, <c>tests/fixtures/content/components.json</c>) holds them to the installed editor.
/// </summary>
internal static partial class EditorComponentText
{
    /// <summary>The indented lines under a top-level <c>info:</c> key (the plugin's own pattern).</summary>
    [GeneratedRegex("^info:[ \\t]*\\n((?:[ \\t]+.*(?:\\n|$))+)", RegexOptions.Multiline)]
    private static partial Regex OpenApiInfo();

    private const string Separator = " · ";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> EmojiTable = new(LoadEmoji);

    /// <summary>The contacts plugin's text: one line per contact (an inline mention has exactly one).</summary>
    public static string Contacts(JsonObject? data, bool inline)
    {
        var contacts = (data?["contacts"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        return string.Join("\n", (inline ? contacts.Take(1) : contacts).Select(Contact));
    }

    private static string Contact(JsonObject contact)
    {
        var parts = new List<string>
        {
            Field(contact, "name"), Field(contact, "title"), Field(contact, "organization"), Field(contact, "department"), Field(contact, "username"),
        };
        parts.AddRange(List(contact, "phones"));
        parts.AddRange(List(contact, "emails"));
        parts.Add(Field(contact, "website"));
        parts.Add(Field(contact, "address"));
        return string.Join(Separator, parts.Where(x => x.Length > 0));
    }

    /// <summary>The chart's title and its grid, a row per line with cells joined by " · ".</summary>
    public static string Chart(JsonObject? data)
    {
        var rows = (data?["grid"] as JsonArray ?? []).Select(row => string.Join(Separator, (row as JsonArray ?? []).Select(cell => Text(cell))));
        return Text(data?["title"]) + "\n" + string.Join("\n", rows);
    }

    /// <summary>Name, size (Czech format, binary units), description and URL of an attachment, the empty ones left out.</summary>
    public static string Attachment(JsonObject? data)
    {
        var size = data?["size"] is JsonValue value && value.TryGetValue<long>(out var bytes) ? Size(bytes) : "";
        return string.Join(Separator, new[] { Field(data, "name"), size, Text(data?["description"]), Field(data, "url") }.Where(x => x.Length > 0));
    }

    /// <summary>
    /// The OpenAPI summary as the plugin writes it (plugins/openapi: <c>lightOpenApiSummary</c>, <c>lightSummary</c>): „title · vversion ·
    /// filename“, or „title · filename“ without a version; the title and version read from the source's <c>info</c> lines (a JSON source:
    /// its <c>"title"</c> and <c>"version"</c> strings) without parsing it, the file name standing in for a missing title.
    /// </summary>
    public static string OpenApi(JsonObject? data)
    {
        var source = Text(data?["source"]).Replace("\r\n", "\n", StringComparison.Ordinal);
        var filename = Text(data?["filename"]);
        var json = filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        var info = json ? source : OpenApiInfo().Match(source) is { Success: true } block ? block.Groups[1].Value : "";
        string Pick(string key) => (json ? new Regex($"\"{key}\"\\s*:\\s*\"([^\"]*)\"") : new Regex($"^[ \\t]+{key}:[ \\t]*['\"]?([^'\"\\r\\n]*?)['\"]?[ \\t]*$", RegexOptions.Multiline))
            .Match(info) is { Success: true } match ? match.Groups[1].Value.Trim() : "";
        var title = Pick("title") is { Length: > 0 } named ? named : filename;
        var version = Pick("version");
        return version.Length > 0 ? title + " · v" + version + " · " + filename : title + " · " + filename;
    }

    /// <summary>The character the emoji plugin writes for a shortcode; null when the editor does not know it (it writes <c>:name:</c>).</summary>
    public static string? Emoji(string name)
    {
        return EmojiTable.Value.GetValueOrDefault(name);
    }

    /// <summary>
    /// The secrets plugin's directive: <c>::secret{…}</c> for a block, <c>:secret{…}</c> inline, with the id and every
    /// presentation attribute that differs from its default. Never a value: the reference holds none.
    /// </summary>
    public static string SecretDirective(JsonObject? attributes, bool block)
    {
        var parts = new List<string> { "secretId=" + Text(attributes?["secretId"]) };
        var field = Text(attributes?["field"]);
        if (field.Length > 0 && field != "password")
        {
            parts.Add("field=" + field);
        }
        if (attributes?["fields"] is JsonArray fields)
        {
            // The directive keeps the stored order (the plugin canonicalizes only the presentation it renders).
            var ordered = fields.Select(x => Text(x)).ToArray();
            parts.Add(ordered.Length == 1 ? "fields=" + ordered[0] : "fields=\"" + string.Join(' ', ordered) + "\"");
        }
        if (attributes?["showCopy"] is JsonValue copy && copy.TryGetValue<bool>(out var showCopy) && !showCopy)
        {
            parts.Add("showCopy=false");
        }
        if (attributes?["showDetails"] is JsonValue details && details.TryGetValue<bool>(out var showDetails) && showDetails)
        {
            parts.Add("showDetails=true");
        }
        if (attributes is not null && attributes.TryGetPropertyValue("autoHideSeconds", out var hide))
        {
            if (hide is null)
            {
                parts.Add("autoHideSeconds=null");
            }
            else if (hide is JsonValue seconds && seconds.TryGetValue<int>(out var value) && value != 60)
            {
                parts.Add("autoHideSeconds=" + value.ToString(CultureInfo.InvariantCulture));
            }
        }
        return (block ? "::" : ":") + "secret{" + string.Join(' ', parts) + "}";
    }

    /// <summary><c>Intl.NumberFormat("cs-CZ", { maximumFractionDigits: 1 })</c> over B, KiB, MiB, GiB.</summary>
    private static string Size(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }
        var unit = Math.Min(3, (int)Math.Floor(Math.Log(bytes) / Math.Log(1024)));
        var value = bytes / Math.Pow(1024, unit);
        var number = value.ToString("#,##0.#", CultureInfo.InvariantCulture).Replace(',', ' ').Replace('.', ',');
        return number + " " + new[] { "B", "KiB", "MiB", "GiB" }[unit];
    }

    /// <summary>A string field as the plugins normalize it: trimmed, empty when absent.</summary>
    private static string Field(JsonObject? node, string name)
    {
        return Text(node?[name]).Trim();
    }

    /// <summary>Trimmed, distinct, in order (the contacts plugin's <c>new Set</c>).</summary>
    private static IEnumerable<string> List(JsonObject node, string name)
    {
        return (node[name] as JsonArray ?? []).Select(x => Text(x).Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal);
    }

    private static string Text(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    }

    private static IReadOnlyDictionary<string, string> LoadEmoji()
    {
        using var stream = typeof(EditorComponentText).Assembly.GetManifestResourceStream("Atlas.Server.Platform.Content.emoji-shortcodes.json")
            ?? throw new InvalidOperationException("The emoji shortcode table is not embedded.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
