using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Platform.Validation;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// A stored rich document (the <c>atlas-rich</c> v1 envelope of the editor) for the server-side conversions:
/// its top-level blocks, and the structural block equality the Markdown round trip is judged by. Its format id, and the legacy one
/// every reader still accepts, are <see cref="MarkdownDocumentValidator.Format"/> and <see cref="MarkdownDocumentValidator.LegacyFormat"/>.
/// </summary>
public sealed class RichDocument
{
    // The Atlas editor schema's mark order: Link (priority 1000), StarterKit's marks, then the rich profile's.
    private static readonly string[] MarkOrder = ["link", "bold", "italic", "strike", "highlight", "underline", "code"];

    private RichDocument(JsonObject envelope, JsonObject document)
    {
        Envelope = envelope;
        Document = document;
        Blocks = (document["content"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
    }

    /// <summary>
    /// How the server writes rich JSON it stores or serves (D-M2): text as raw UTF-8, as the web editor stores it, so an unchanged
    /// document is the same string and a Czech page is not inflated by <c>\u0159</c> escapes; as deep as a stored document may be.
    /// </summary>
    public static readonly JsonSerializerOptions JsonWrite = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = MarkdownDocumentValidator.JsonMaxDepth,
    };

    /// <summary>The stored envelope (<c>format</c>, <c>version</c>, <c>document</c>, <c>plugins</c>); the merge keeps all but the document.</summary>
    internal JsonObject Envelope { get; }

    /// <summary>The <c>doc</c> node.</summary>
    public JsonObject Document { get; }

    /// <summary>The top-level blocks in document order.</summary>
    public IReadOnlyList<JsonObject> Blocks { get; }

    /// <summary>Reads the stored envelope with the content migrations applied (<see cref="LegacyDocumentRepair"/>, as the editor opens
    /// it); anything but <c>atlas-rich</c> (or the legacy <c>scalesoft-rich</c>) version 1 with a <c>doc</c> is refused.</summary>
    /// <exception cref="InvalidDataException">Not a rich document; the message never echoes the input.</exception>
    public static RichDocument Parse(string contentJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(contentJson, documentOptions: MarkdownDocumentValidator.JsonOptions);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
        if (root is not JsonObject envelope || !MarkdownDocumentValidator.IsFormat(Text(envelope["format"]))
            || envelope["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != 1
            || envelope["document"] is not JsonObject document || TypeOf(document) != "doc")
        {
            throw Invalid();
        }
        LegacyDocumentRepair.Repair(document);
        return new RichDocument(envelope, document);
    }

    /// <summary>
    /// The stored form of an editor document: an attribute of a node or mark at its schema default is left out, which the editor reads
    /// back the same (ProseMirror's nodeFromJSON fills defaults, and so do the server's seed and readers, as for imported pages). The
    /// editor's save writes every default; on a large table page that is over half the JSON. Attributes without a default and types or
    /// attributes the schema does not know are kept. The same string when nothing is left out.
    /// </summary>
    public static string WithoutDefaultAttributes(string contentJson)
    {
        var root = JsonNode.Parse(contentJson, documentOptions: MarkdownDocumentValidator.JsonOptions);
        return root?["document"] is JsonObject document && DropDefaults(document) ? root.ToJsonString(JsonWrite) : contentJson;
    }

    /// <summary><see cref="WithoutDefaultAttributes(string)"/> of a <c>doc</c> node, in place.</summary>
    public static JsonNode? WithoutDefaultAttributes(JsonNode? document)
    {
        if (document is JsonObject node)
        {
            DropDefaults(node);
        }
        return document;
    }

    /// <summary>The <c>doc</c> node of rich JSON (a stored envelope or a bare document, as a projection holds it) without its attributes at
    /// their default: two documents compared so are the same whether the editor or the store wrote them.</summary>
    public static JsonNode? DocumentWithoutDefaults(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: MarkdownDocumentValidator.JsonOptions);
        return WithoutDefaultAttributes(root is JsonObject envelope && envelope["document"] is JsonObject document ? document : root);
    }

    private static bool DropDefaults(JsonObject node)
    {
        var changed = DropDefaultAttributes(node);
        foreach (var mark in (node["marks"] as JsonArray ?? []).OfType<JsonObject>())
        {
            changed |= DropDefaultAttributes(mark);
        }
        foreach (var child in (node["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            changed |= DropDefaults(child);
        }
        return changed;
    }

    private static bool DropDefaultAttributes(JsonObject item)
    {
        if (item["attrs"] is not JsonObject attrs || Text(item["type"]) is not { } type)
        {
            return false;
        }
        var defaults = attrs.Where(attr => IsDefault(type, attr.Key, attr.Value)).Select(attr => attr.Key).ToArray();
        foreach (var key in defaults)
        {
            attrs.Remove(key);
        }
        if (defaults.Length > 0 && attrs.Count == 0)
        {
            item.Remove("attrs");
        }
        return defaults.Length > 0;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Type, string Attribute), (bool Declared, JsonNode? Value)> Defaults = new();

    private static (bool Declared, JsonNode? Value) DefaultOf(string type, string attribute) => Defaults.GetOrAdd((type, attribute),
        key => HostDocumentValidator.Instance.Schema.AttributeDefault(key.Type, key.Attribute) is { } fallback
            ? (true, JsonSerializer.SerializeToNode(fallback))
            : (false, null));

    // Only a plain default (null, a boolean, number or string), as the editor: a default object (a chart's data) stays as written.
    private static bool IsDefault(string type, string attribute, JsonNode? value)
        => DefaultOf(type, attribute) is { Declared: true } fallback && fallback.Value is null or JsonValue && JsonNode.DeepEquals(value, fallback.Value);

    /// <summary>An attribute of a stored node or mark: the stored form leaves out one at its schema default, so absent reads as that default.</summary>
    public static JsonNode? Attribute(JsonObject attributes, string type, string attribute)
        => attributes.TryGetPropertyValue(attribute, out var value) ? value : DefaultOf(type, attribute).Value?.DeepClone();

    /// <summary>Position of a mark in the editor schema; ProseMirror keeps a text node's marks in this order.</summary>
    public static int MarkRank(JsonNode mark)
    {
        var index = Array.IndexOf(MarkOrder, TypeOf(mark));
        return index < 0 ? MarkOrder.Length : index;
    }

    public static string TypeOf(JsonNode? node)
    {
        return Text(node?["type"]) ?? "";
    }

    /// <summary>
    /// Structural equality of two nodes as Markdown sees them: heading identities (carried separately) and list
    /// tightness do not count, nor do attributes at their default (null, a single cell span, an empty code language,
    /// a link's target and rel). Anything else Markdown cannot carry, such as a cell background or a text alignment,
    /// makes the nodes differ.
    /// </summary>
    public static bool StructurallyEqual(JsonNode? left, JsonNode? right)
    {
        return JsonNode.DeepEquals(Comparable(left), Comparable(right));
    }

    /// <summary>
    /// The form <see cref="StructurallyEqual"/> compares: normalized attributes, ordered marks, merged text, and object keys in
    /// ordinal order, so two equal nodes also write equal JSON whatever key order they were built in (the parser writes a text
    /// node's text before its marks, the editor after them).
    /// </summary>
    public static JsonNode? Comparable(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                return new JsonArray(array.Select(Comparable).ToArray());
            case JsonObject value:
            {
                var type = TypeOf(value);
                var result = new JsonObject();
                foreach (var (key, item) in value)
                {
                    switch (key)
                    {
                        case "attrs" when item is JsonObject attributes:
                            if (Attributes(type, attributes) is { Count: > 0 } kept)
                            {
                                result["attrs"] = kept;
                            }
                            break;
                        case "marks" when item is JsonArray marks:
                            if (marks.OfType<JsonObject>().OrderBy(MarkRank).Select(Mark).ToArray() is { Length: > 0 } ordered)
                            {
                                result["marks"] = new JsonArray(ordered);
                            }
                            break;
                        case "content" when item is JsonArray content:
                            if (MergeText(content.Select(Comparable)) is { Count: > 0 } children)
                            {
                                result["content"] = children;
                            }
                            break;
                        default:
                            if (item is not null)
                            {
                                result[key] = item.DeepClone();
                            }
                            break;
                    }
                }
                return Sorted(result);
            }
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// Attributes compared as absent at their schema default, so a node stored without them (the stored form) equals one written with
    /// them (the editor's toJSON, the Markdown parser): a heading's level 1, a list's start 1, a cell's single span, the requirement
    /// heading's and the dependency line's attributes Markdown carries none of.
    /// </summary>
    private static JsonObject Attributes(string type, JsonObject attributes)
    {
        var result = new JsonObject();
        foreach (var (key, item) in attributes)
        {
            if (item is not null && key is not ("headingId" or "tight") && !IsDefault(type, key, item))
            {
                result[key] = Comparable(item);
            }
        }
        switch (type)
        {
            case "tableHeader" or "tableCell":
            {
                // The editor keeps both names for one alignment; Markdown carries one.
                var align = Text(result["textAlign"]) ?? Text(result["align"]);
                result.Remove("align");
                result.Remove("textAlign");
                if (align is not null)
                {
                    result["textAlign"] = align;
                }
                break;
            }
            case "codeBlock" when Text(result["language"]) is "":
                result.Remove("language");
                break;
        }
        return Sorted(result);
    }

    private static JsonObject Sorted(JsonObject node)
    {
        var sorted = new JsonObject();
        foreach (var key in node.Select(x => x.Key).Order(StringComparer.Ordinal).ToArray())
        {
            var item = node[key];
            node.Remove(key);
            sorted[key] = item;
        }
        return sorted;
    }

    private static JsonNode Mark(JsonObject mark)
    {
        var type = TypeOf(mark);
        if (type != "link")
        {
            return Comparable(mark)!;
        }
        // A link is its target and title; target, rel and class are the editor's fixed defaults.
        var attributes = new JsonObject { ["href"] = Text(mark["attrs"]?["href"]) ?? "" };
        if (Text(mark["attrs"]?["title"]) is { Length: > 0 } title)
        {
            attributes["title"] = title;
        }
        return new JsonObject { ["type"] = "link", ["attrs"] = attributes };
    }

    /// <summary>
    /// Adjacent text with equal marks is one node, as ProseMirror keeps it. The nodes must have no parent (a node that belongs
    /// to an array cannot be added to another one); detach them first.
    /// </summary>
    internal static JsonArray MergeText(IEnumerable<JsonNode?> nodes)
    {
        var result = new JsonArray();
        foreach (var node in nodes)
        {
            if (result.Count == 0 || !TryMergeText(result[^1], node))
            {
                result.Add(node);
            }
        }
        return result;
    }

    /// <summary>
    /// Adjacent text with equal marks is one node, as ProseMirror keeps it: when both nodes are text with equal marks, the
    /// text of <paramref name="current"/> is appended to <paramref name="previous"/> and the caller drops <paramref name="current"/>.
    /// A text node whose text is missing or not a string counts as empty; no caller can produce one (the copies of the merge, the
    /// import and the strict parser this replaced would have thrown), so only stored JSON can meet this tolerance.
    /// </summary>
    /// <returns>True when <paramref name="current"/> was merged into <paramref name="previous"/>.</returns>
    internal static bool TryMergeText(JsonNode? previous, JsonNode? current)
    {
        if (current is not JsonObject next || TypeOf(next) != "text" || previous is not JsonObject last || TypeOf(last) != "text"
            || !JsonNode.DeepEquals(last["marks"], next["marks"]))
        {
            return false;
        }
        last["text"] = (Text(last["text"]) ?? "") + (Text(next["text"]) ?? "");
        return true;
    }

    private static string? Text(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static InvalidDataException Invalid()
    {
        return new InvalidDataException("Obsah stránky není dokument editoru.");
    }
}
