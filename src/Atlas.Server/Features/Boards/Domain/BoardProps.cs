using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Boards.Domain;

/// <summary>
/// The typed props of each kind and the board's settings (interaction spec §4), checked strictly: a key the kind does not have, a
/// value outside its rule or a missing required key is invalid_board_change. <c>create</c> carries the full props, <c>set</c>
/// and <c>settings</c> a part that is merged per key (a later batch wins per key, <c>null</c> removes an optional key). Stored
/// and sent in one canonical form: keys in ordinal order at every depth, written by BoardJson.
/// </summary>
public static class BoardProps
{
    public const string Empty = "{}";

    /// <summary>The JSON of one editor block on a board, in UTF-8 bytes.</summary>
    public const int MaxBlockBytes = 64 * 1024;

    /// <summary>What <see cref="WithDefaults"/> shows for a board whose settings were never changed.</summary>
    public const string DefaultSettings = """{"background":"dots","gridSize":24,"snapToGrid":false}""";

    private static readonly Func<JsonNode, bool> Align = OneOf("left", "center", "right");
    private static readonly Func<JsonNode, bool> Side = OneOf("top", "right", "bottom", "left");

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, Key>> Kinds = new Dictionary<string, IReadOnlyDictionary<string, Key>>(StringComparer.Ordinal)
    {
        [BoardItemKinds.Note] = Keys(("fontSize", false, OneOf("auto", "s", "m", "l"))),
        [BoardItemKinds.Text] = Keys(("fontSize", true, OneOf("s", "m", "l", "xl")), ("align", true, Align), ("bold", false, Boolean)),
        [BoardItemKinds.Shape] = Keys(("shape", true, OneOf("rect", "rounded", "ellipse", "diamond")), ("fontSize", false, OneOf("auto", "s", "m", "l", "xl")),
            ("align", false, Align)),
        [BoardItemKinds.Frame] = Keys(),
        [BoardItemKinds.Group] = Keys(("collapsed", false, Boolean)),
        // "product" / "service": a deployment's product (its heading's anchor) or service (its name's slug), drawn live from the page.
        [BoardItemKinds.Page] = Keys(("display", true, OneOf("card", "preview", "product", "service")), ("anchor", false, Anchor)),
        [BoardItemKinds.Block] = Keys(("block", true, IsBlock)),
        // The object a card shows is fixed when it is placed; only its view changes.
        [BoardItemKinds.Object] = Keys(("objectId", true, ObjectId), ("typeId", true, TypeId), ("view", true, OneOf("card", "compact")))
            .WithFixed("objectId", "typeId"),
        [BoardItemKinds.Connector] = Keys(("fromSide", false, Side), ("toSide", false, Side), ("route", true, OneOf("elbow", "curved", "straight")),
            ("arrowStart", true, OneOf("none", "arrow")), ("arrowEnd", true, OneOf("none", "arrow")), ("dash", true, OneOf("solid", "dashed")),
            ("state", false, OneOf("waiting", "done", "blocked")),
            // The quick styles: a flow along the arrow's direction and the line's thickness (none: the plain 2 px line).
            ("animated", false, Boolean), ("thickness", false, OneOf("s", "m", "l"))),
        // What the derived list shows, and its page or object (people and waiting: one page instead of the space; usage: the object).
        [BoardItemKinds.Links] = Keys(("show", true, OneOf("people", "waiting", "usage")), ("objectId", false, ObjectId))
    };

    private static readonly IReadOnlyDictionary<string, Key> Settings = Keys(("background", true, OneOf("dots", "lines", "none", "timeline")),
        ("snapToGrid", true, Boolean), ("gridSize", true, node => Number(node) is 8 or 16 or 24 or 32 or 48), ("timeline", false, IsTimeline));

    public static bool IsKind(string? kind)
    {
        return kind is not null && Kinds.ContainsKey(kind);
    }

    /// <summary>The full props of a new item as canonical text, or null when they are not the kind's.</summary>
    public static string? Create(string kind, JsonObject props)
    {
        var keys = Kinds[kind];
        return props.All(prop => keys.TryGetValue(prop.Key, out var key) && prop.Value is not null && key.Valid(prop.Value))
               && keys.All(key => !key.Value.Required || props.ContainsKey(key.Key))
            ? BoardJson.Write(Canonical(props))
            : null;
    }

    /// <summary>The stored props with <paramref name="patch"/> merged per key, as canonical text, or null when the patch is not
    /// the kind's (an unknown key, an invalid value, a required key removed).</summary>
    public static string? Merge(string kind, string current, JsonObject patch)
    {
        return Merge(Kinds[kind], current, patch);
    }

    /// <summary>The stored settings (only the keys ever changed) with <paramref name="patch"/> merged, or null when it is invalid.</summary>
    public static string? MergeSettings(string current, JsonObject patch)
    {
        return Merge(Settings, current, patch);
    }

    /// <summary>The settings a reader sees: the defaults, overridden by what the board changed.</summary>
    public static string WithDefaults(string settings)
    {
        var shown = JsonNode.Parse(DefaultSettings)!.AsObject();
        foreach (var (name, value) in JsonNode.Parse(settings)!.AsObject())
        {
            shown[name] = value?.DeepClone();
        }
        return BoardJson.Write(Canonical(shown));
    }

    /// <summary>A copy with the keys in ordinal order at every depth; arrays keep their order.</summary>
    public static JsonObject Canonical(JsonObject props)
    {
        return (JsonObject)Sorted(props)!;
    }

    /// <summary>Stored text (jsonb answers in its own key order and spacing) as canonical text.</summary>
    public static string Canonical(string json)
    {
        return BoardJson.Write(Canonical(JsonNode.Parse(json)!.AsObject()));
    }

    private static string? Merge(IReadOnlyDictionary<string, Key> keys, string current, JsonObject patch)
    {
        var merged = JsonNode.Parse(current)!.AsObject();
        foreach (var (name, value) in patch)
        {
            if (!keys.TryGetValue(name, out var key) || key.Fixed || (value is null ? key.Required : !key.Valid(value)))
            {
                return null;
            }
            if (value is null)
            {
                merged.Remove(name);
            }
            else
            {
                merged[name] = value.DeepClone();
            }
        }
        return BoardJson.Write(Canonical(merged));
    }

    private static JsonNode? Sorted(JsonNode? node)
    {
        return node switch
        {
            JsonObject value => new JsonObject(value.OrderBy(prop => prop.Key, StringComparer.Ordinal).Select(prop => KeyValuePair.Create(prop.Key, Sorted(prop.Value)))),
            JsonArray value => new JsonArray(value.Select(Sorted).ToArray()),
            _ => node?.DeepClone()
        };
    }

    /// <summary>
    /// One editor node as a page holds it (a contact card, a chart, a diagram, a callout, an inline YouTrack issue…), stored as
    /// sent: a block passes the page rules as the document of just this block, an inline node as that of a paragraph holding just
    /// it (Rules.ValidateDocument with the plugins of CollaborationSchema.KnownHostSchemas[^1]; the schema decides which it is). A
    /// password reference is refused: its protection belongs to a page and the peers of its document.
    /// </summary>
    private static bool IsBlock(JsonNode node)
    {
        if (node is not JsonObject block)
        {
            return false;
        }
        var json = BoardJson.Write(block);
        if (Encoding.UTF8.GetByteCount(json) > MaxBlockBytes)
        {
            return false;
        }
        using (var parsed = JsonDocument.Parse(json))
        {
            if (SecretReferenceContent.ContainsReservedContent(parsed.RootElement))
            {
                return false;
            }
        }
        return BlockMarkdown(block) is not null;
    }

    /// <summary>
    /// What search reads of an item: its text as written (Markdown), for a block the Markdown the page writer makes of it (the
    /// components' readable text, as a page's search body has it), nothing for a page card (its page is found as a page).
    /// </summary>
    public static string? SearchText(string kind, string? text, string props)
    {
        if (kind == BoardItemKinds.Block)
        {
            return JsonNode.Parse(props)?["block"] is JsonObject block ? BlockMarkdown(block) : null;
        }
        return kind is BoardItemKinds.Page or BoardItemKinds.Object || string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>The Markdown of a document holding just the block, or of a paragraph holding just the inline node; null when the
    /// page rules refuse both.</summary>
    private static string? BlockMarkdown(JsonObject block)
    {
        return Markdown(block.DeepClone()) ?? Markdown(new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(block.DeepClone()) });
    }

    private static string? Markdown(JsonNode content)
    {
        // A block stored before a content migration (a disclosure, 3.6: a heading and its body) reads as the editor opens it.
        var document = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(content) };
        LegacyDocumentRepair.Repair(document);
        var envelope = new JsonObject
        {
            ["format"] = MarkdownDocumentValidator.Format,
            ["version"] = 1,
            ["plugins"] = new JsonArray(CollaborationSchema.KnownHostSchemas[^1].Plugins
                .Select(plugin => (JsonNode)new JsonObject { ["id"] = plugin.Id, ["version"] = plugin.Version }).ToArray()),
            ["document"] = document
        };
        try
        {
            return Rules.ValidateDocument(BoardJson.Write(envelope)).Markdown;
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>The object an object card's props name (its id and type), or null when they name none.</summary>
    public static (Guid Id, string TypeId)? ObjectTarget(JsonObject? props)
    {
        return props?["objectId"] is { } id && ObjectId(id) && props["typeId"] is { } type && TypeId(type) ? (Guid.Parse(Text(id)!), Text(type)!) : null;
    }

    private static bool ObjectId(JsonNode node)
    {
        return Guid.TryParseExact(Text(node), "D", out var id) && id != Guid.Empty;
    }

    /// <summary>A type id as the registry names them: dot-separated lower-case segments (atlas.contact, projector.requirement).</summary>
    private static bool TypeId(JsonNode node)
    {
        return Text(node) is { Length: <= 100 } type && System.Text.RegularExpressions.Regex.IsMatch(type, @"^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*)+\z");
    }

    /// <summary>A section of the page by its stable heading id (heading-anchors; a temporary projected id is not one).</summary>
    private static bool Anchor(JsonNode node)
    {
        return Text(node) is { } id && !id.EndsWith('\n') && HeadingIds.IsValid(id) && !HeadingIds.IsTemporary(id);
    }

    /// <summary>The timeline background: its origin (the date at x = 0) and scale, nothing else.</summary>
    private static bool IsTimeline(JsonNode node)
    {
        return node is JsonObject timeline && timeline.Count == 2
            && timeline["origin"] is { } origin && Text(origin) is { } date && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && timeline["pxPerDay"] is { } scale && Number(scale) is >= 0.01 and <= 2000;
    }

    private static Func<JsonNode, bool> OneOf(params string[] values)
    {
        return node => Text(node) is { } text && values.Contains(text, StringComparer.Ordinal);
    }

    private static bool Boolean(JsonNode node)
    {
        return node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
    }

    private static string? Text(JsonNode node)
    {
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }

    // NaN and the infinities never come from JSON; a number outside double fails to parse.
    private static double? Number(JsonNode node)
    {
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var number) ? number : null;
    }

    private static IReadOnlyDictionary<string, Key> Keys(params (string Name, bool Required, Func<JsonNode, bool> Valid)[] keys)
    {
        return keys.ToDictionary(key => key.Name, key => new Key(key.Required, key.Valid), StringComparer.Ordinal);
    }

    private static Dictionary<string, Key> WithFixed(this IReadOnlyDictionary<string, Key> keys, params string[] names)
    {
        return keys.ToDictionary(key => key.Key, key => names.Contains(key.Key) ? key.Value with { Fixed = true } : key.Value, StringComparer.Ordinal);
    }

    /// <summary>Fixed: set on create, never by a later set.</summary>
    private sealed record Key(bool Required, Func<JsonNode, bool> Valid, bool Fixed = false);
}
