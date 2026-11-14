using System.Text.Json.Nodes;
using Atlas.Server.Platform.Validation;

namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>
/// The few ProseMirror model functions the browser seed's ports need, over the stored JSON (prosemirror-model 1.25.11: Node.nodeSize,
/// Fragment.nodesBetween, Fragment.textBetween, Node.textContent), with the node kinds of the host schema (host-markdown-schema.json).
/// </summary>
internal static class SeedNodes
{
    private sealed record Spec(bool Leaf, bool Inline, bool Textblock, HashSet<string> Marks);

    private static readonly Lazy<Dictionary<string, Spec>> Specs = new(() =>
    {
        using var stream = typeof(HostDocumentValidator).Assembly.GetManifestResourceStream("Atlas.Server.Platform.Validation.host-markdown-schema.json")
            ?? throw new InvalidOperationException("The host editor schema is not embedded.");
        var manifest = JsonNode.Parse(stream)!;
        return manifest["nodes"]!.AsArray().ToDictionary(node => node!["name"]!.GetValue<string>(), node =>
        {
            var content = node!["content"]?.GetValue<string>() ?? "";
            var inline = node["inline"]?.GetValue<bool>() ?? false;
            return new Spec(content.Length == 0, inline, !inline && (content.StartsWith("inline", StringComparison.Ordinal) || content.StartsWith("text", StringComparison.Ordinal)),
                [.. (node["marks"] as JsonArray ?? []).Select(mark => mark!.GetValue<string>())]);
        }, StringComparer.Ordinal);
    });

    public static string Type(JsonNode? node) => node?["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : "";

    public static JsonArray? Content(JsonNode? node) => node?["content"] as JsonArray;

    public static bool IsText(JsonNode? node) => Type(node) == "text";

    public static string Text(JsonNode? node) => node?["text"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    public static bool IsLeaf(JsonNode? node) => IsText(node) || (Specs.Value.TryGetValue(Type(node), out var spec) && spec.Leaf);

    public static bool IsInline(JsonNode? node) => IsText(node) || (Specs.Value.TryGetValue(Type(node), out var spec) && spec.Inline);

    public static bool IsTextblock(JsonNode? node) => Specs.Value.TryGetValue(Type(node), out var spec) && spec.Textblock;

    public static bool AllowsMark(JsonNode? node, string mark) => Specs.Value.TryGetValue(Type(node), out var spec) && spec.Marks.Contains(mark);

    /// <summary>A JSON number, whether parsed or written by these ports; null for anything else.</summary>
    public static double? JsNumber(JsonNode? node) => node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
        ? double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : null;

    public static bool HasMark(JsonNode? node, string mark) => (node?["marks"] as JsonArray ?? []).Any(item => Type(item) == mark);

    // Node.nodeSize
    public static int NodeSize(JsonNode? node) => IsText(node) ? Text(node).Length : IsLeaf(node) ? 1 : 2 + ContentSize(node);

    public static int ContentSize(JsonNode? node) => (Content(node) ?? []).Sum(NodeSize);

    /// <summary>Fragment.nodesBetween of a node's content: <paramref name="f"/> gets each node overlapping [from, to] with its position and
    /// parent, and returns false to skip its children.</summary>
    public static void NodesBetween(JsonNode parent, int from, int to, Func<JsonNode, int, JsonNode, bool> f, int nodeStart = 0)
    {
        var content = Content(parent) ?? [];
        for (int i = 0, pos = 0; pos < to && i < content.Count; i++)
        {
            var child = content[i]!;
            var end = pos + NodeSize(child);
            if (end > from && f(child, nodeStart + pos, parent) && ContentSize(child) > 0)
            {
                var start = pos + 1;
                NodesBetween(child, Math.Max(0, from - start), Math.Min(ContentSize(child), to - start), f, nodeStart + start);
            }
            pos = end;
        }
    }

    /// <summary>Fragment.textBetween with a string <paramref name="leafText"/> (no node of the host schema defines spec.leafText).</summary>
    public static string TextBetween(JsonNode parent, int from, int to, string? blockSeparator, string? leafText)
    {
        var text = new System.Text.StringBuilder();
        var first = true;
        NodesBetween(parent, from, to, (node, pos, _) =>
        {
            var nodeText = IsText(node) ? Text(node)[(Math.Max(from, pos) - pos)..Math.Min(Text(node).Length, to - pos)]
                : !IsLeaf(node) ? "" : leafText ?? "";
            if (!IsInline(node) && ((IsLeaf(node) && nodeText.Length > 0) || IsTextblock(node)) && !string.IsNullOrEmpty(blockSeparator))
            {
                if (first) first = false;
                else text.Append(blockSeparator);
            }
            text.Append(nodeText);
            return true;
        });
        return text.ToString();
    }

    /// <summary>A character JavaScript's \s (and String.prototype.trim) reads as white space.</summary>
    public static bool IsJsSpace(char c)
    {
        var ranges = LinkifyData.Space;
        for (var i = 0; i < ranges.Length; i += 2)
        {
            if (c >= ranges[i] && c <= ranges[i + 1]) return true;
        }
        return false;
    }

    /// <summary>Node.textContent: the text of its text nodes (leaves add spec.leafText, which no host node defines).</summary>
    public static string TextContent(JsonNode? node)
        => IsText(node) ? Text(node) : string.Concat((Content(node) ?? []).Select(TextContent));
}
