using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Markdown.Sync;

namespace Atlas.Server.Platform.Validation;

/// <summary>
/// The document side of password references (WP4 V6; persisted contract in the editor's secrets plugin README, schema v4). A
/// reference is a <c>secretReference</c> (block) or <c>secretInlineReference</c> (inline) atom whose attributes name the Atlas
/// secret id and how it is presented; every value, the path and the notes come from the host at release time. Anything else in
/// such a node (an unknown attribute such as an inline value, text content, a label) breaks the contract and is rejected, so no
/// ingestion path can store a value in a document. The protection mode lives on the server record, never here.
/// </summary>
public static partial class SecretReferenceContent
{
    public const string NodeType = "secretReference";
    public const string InlineNodeType = "secretInlineReference";
    public const string PluginId = "secrets";

    /// <summary>Every attribute a reference may carry; absent attributes read as their defaults.</summary>
    public static readonly FrozenSet<string> Attributes =
        new[] { "secretId", "field", "fields", "showCopy", "showDetails", "autoHideSeconds" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Fields =
        new[] { "title", "path", "username", "password", "url", "notes" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Keys of a reference node object in rich JSON: an atom has no content or text; an inline atom may carry marks.</summary>
    private static readonly FrozenSet<string> NodeKeys = new[] { "type", "attrs", "marks" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<Guid> None = FrozenSet<Guid>.Empty;

    public static bool IsSecretNodeType(string? nodeType) => nodeType is NodeType or InlineNodeType;

    /// <summary>
    /// Cheap check on raw JSON before parsing it: false only when no reference can be inside (both node type names contain
    /// "ecret"; a name that hides it must escape one of those letters as c, e, r or t). Other escapes, such as
    /// the diacritics and quotes stored Czech pages are full of, cannot spell it and no longer force a parse of every page read.
    /// </summary>
    public static bool MayContainReferences(string? json) => json is not null
        && (json.Contains("ecret", StringComparison.Ordinal) || EscapedNameLetter().IsMatch(json));

    [GeneratedRegex(@"\\u00(?:6[35]|7[24])", RegexOptions.CultureInvariant)]
    private static partial Regex EscapedNameLetter();

    /// <summary>The distinct secret ids referenced anywhere in the JSON (null or no reference: none).</summary>
    /// <exception cref="InvalidDataException">A reference breaks the contract; the message never echoes the input.</exception>
    public static IReadOnlySet<Guid> Collect(string? json)
    {
        if (!MayContainReferences(json))
        {
            return None;
        }
        try
        {
            using var document = JsonDocument.Parse(json!, MarkdownDocumentValidator.JsonOptions);
            return Collect(document.RootElement);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    /// <inheritdoc cref="Collect(string?)"/>
    public static IReadOnlySet<Guid> Collect(JsonElement value)
    {
        var ids = new HashSet<Guid>();
        Walk(value, ids);
        return ids;
    }

    /// <summary>
    /// The attribute rules shared by rich JSON and the collaboration guard: only known keys, each at most once, with the plugin's
    /// types and ranges. <paramref name="requireId"/> for a new node; an attribute rewrite of an existing node may omit the id.
    /// </summary>
    public static bool TryValidateAttributes(IEnumerable<KeyValuePair<string, JsonElement>> attributes, bool requireId, out Guid? secretId)
    {
        secretId = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            if (!Attributes.Contains(key) || !seen.Add(key) || !ValidValue(key, value, out var id))
            {
                return false;
            }
            secretId ??= id;
        }
        return !requireId || secretId is not null;
    }

    /// <summary>
    /// A copy without the references <paramref name="visible"/> refuses: a block reference becomes an empty paragraph (block
    /// containers keep valid content), an inline one disappears. Without any reference left the secrets plugin requirement goes
    /// too. The input is returned unchanged when nothing is removed.
    /// </summary>
    public static string Strip(string json, Func<Guid, bool> visible, out int removed)
    {
        removed = 0;
        if (!MayContainReferences(json))
        {
            return json;
        }
        JsonNode root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: MarkdownDocumentValidator.JsonOptions) ?? throw Invalid();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Invalid();
        }
        var remaining = 0;
        StripNode(root, visible, ref removed, ref remaining);
        if (removed == 0)
        {
            return json;
        }
        if (remaining == 0 && root is JsonObject envelope && envelope["plugins"] is JsonArray plugins)
        {
            for (var index = plugins.Count - 1; index >= 0; index--)
            {
                if (plugins[index] is JsonObject plugin && plugin["id"] is JsonValue id && id.TryGetValue<string>(out var name) && name == PluginId)
                {
                    plugins.RemoveAt(index);
                }
            }
        }
        return root.ToJsonString(Atlas.Server.Platform.Content.RichDocument.JsonWrite);
    }

    /// <summary>Whether the JSON declares a reference node or the secrets plugin anywhere (duplicate keys included).</summary>
    public static bool ContainsReservedContent(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Any(ContainsReservedContent);
        if (value.ValueKind != JsonValueKind.Object) return false;
        // Enumerate all properties: duplicate keys must not hide a reserved declaration.
        return value.EnumerateObject().Any(property =>
            property.Name == "type" && property.Value.ValueKind == JsonValueKind.String && IsSecretNodeType(property.Value.GetString())
            || property.Name == "plugins" && ContainsSecretRequirement(property.Value)
            || ContainsReservedContent(property.Value));
    }

    private static bool ContainsSecretRequirement(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() == PluginId,
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsSecretRequirement),
        JsonValueKind.Object => value.EnumerateObject().Any(property =>
            property.Name == "id" && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == PluginId
            || property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object && ContainsSecretRequirement(property.Value)),
        _ => false
    };

    private static void Walk(JsonElement value, HashSet<Guid> ids)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                Walk(item, ids);
            }
            return;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        // Every "type" counts: a duplicate key must not hide a reference behind another node type.
        if (value.EnumerateObject().Any(p => p.Name == "type" && p.Value.ValueKind == JsonValueKind.String && IsSecretNodeType(p.Value.GetString())))
        {
            ids.Add(ValidateNode(value));
            return;
        }
        foreach (var property in value.EnumerateObject())
        {
            Walk(property.Value, ids);
        }
    }

    private static Guid ValidateNode(JsonElement node)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        JsonElement? attrs = null;
        foreach (var property in node.EnumerateObject())
        {
            if (!NodeKeys.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw Invalid();
            }
            if (property.Name == "attrs")
            {
                attrs = property.Value;
            }
            else if (property.Name == "marks" && (property.Value.ValueKind != JsonValueKind.Array || ContainsReservedContent(property.Value)))
            {
                throw Invalid();
            }
        }
        if (attrs is not { ValueKind: JsonValueKind.Object } values
            || !TryValidateAttributes(values.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value)), true, out var id))
        {
            throw Invalid();
        }
        return id!.Value;
    }

    private static bool ValidValue(string key, JsonElement value, out Guid? secretId)
    {
        secretId = null;
        switch (key)
        {
            case "secretId":
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || !CanonicalId().IsMatch(text))
                {
                    return false;
                }
                secretId = Guid.Parse(text);
                return true;
            case "field":
                return value.ValueKind == JsonValueKind.String && Fields.Contains(value.GetString()!);
            case "fields":
                if (value.ValueKind == JsonValueKind.Null)
                {
                    return true;
                }
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 6)
                {
                    return false;
                }
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                return value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && Fields.Contains(item.GetString()!) && distinct.Add(item.GetString()!));
            case "showCopy":
            case "showDetails":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case "autoHideSeconds":
                return value.ValueKind == JsonValueKind.Null
                    || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var seconds) && seconds is >= 5 and <= 3600;
            default:
                return false;
        }
    }

    private static void StripNode(JsonNode node, Func<Guid, bool> visible, ref int removed, ref int remaining)
    {
        if (node is JsonArray array)
        {
            for (var index = array.Count - 1; index >= 0; index--)
            {
                if (array[index] is not { } child)
                {
                    continue;
                }
                if (SecretType(child) is not { } type)
                {
                    StripNode(child, visible, ref removed, ref remaining);
                }
                else if (Keeps(child, visible))
                {
                    remaining++;
                }
                else
                {
                    removed++;
                    if (type == NodeType)
                    {
                        array[index] = new JsonObject { ["type"] = "paragraph" };
                    }
                    else
                    {
                        array.RemoveAt(index);
                    }
                }
            }
        }
        else if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (value is null)
                {
                    continue;
                }
                if (SecretType(value) is null)
                {
                    StripNode(value, visible, ref removed, ref remaining);
                }
                else if (Keeps(value, visible))
                {
                    remaining++;
                }
                else
                {
                    removed++;
                    obj.Remove(key);
                }
            }
        }
    }

    private static string? SecretType(JsonNode node) =>
        node is JsonObject obj && obj["type"] is JsonValue type && type.TryGetValue<string>(out var name) && IsSecretNodeType(name) ? name : null;

    private static bool Keeps(JsonNode node, Func<Guid, bool> visible) =>
        node["attrs"]?["secretId"] is JsonValue id && id.TryGetValue<string>(out var text) && CanonicalId().IsMatch(text) && visible(Guid.Parse(text));

    private static InvalidDataException Invalid() => new("A password reference in the document is not valid.");

    /// <summary>The plugin's rule: canonical lowercase RFC 9562 UUID, version 1–8, variant 8/9/a/b.</summary>
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalId();
}
