using System.Text.Json;

namespace Atlas.Yjs.ProseMirror;

/// <summary>An attribute of a node or mark type: its default, when it has one (computeAttrs takes it for an absent value).</summary>
internal sealed record PmAttribute(string Name, bool HasDefault, JsonElement? Default)
{
    /// <summary>AttributeSpec.default as JavaScript holds it: one value every node without its own shares.</summary>
    public object? DefaultValue { get; } = HasDefault && Default is { } value ? Js.FromJson(value) : null;
}

/// <summary>A node type: its name and attributes in declaration order (ProseMirror's attrs order).</summary>
internal sealed record PmNodeType(string Name, IReadOnlyList<PmAttribute> Attributes);

/// <summary>A mark type: rank (its place in the schema), whether it excludes itself (MarkType.excludes(self)), attributes.</summary>
internal sealed record PmMarkType(string Name, int Rank, bool ExcludesItself, IReadOnlyList<PmAttribute> Attributes);

/// <summary>What the port needs of a ProseMirror schema, given by the host (Atlas.Markdown.Sync builds it from its manifest).</summary>
internal sealed class PmSchema(string topNode, IReadOnlyDictionary<string, PmNodeType> nodes, IReadOnlyDictionary<string, PmMarkType> marks, int maxDepth)
{
    public string TopNode { get; } = topNode;
    public IReadOnlyDictionary<string, PmNodeType> Nodes { get; } = nodes;
    public IReadOnlyDictionary<string, PmMarkType> Marks { get; } = marks;
    /// <summary>Nesting refused beyond it (the recursive ports would otherwise follow a hostile document down).</summary>
    public int MaxDepth { get; } = maxDepth;
}

/// <summary>A ProseMirror mark: its type and computed attributes.</summary>
internal sealed class PmMark(PmMarkType type, JsObject attrs)
{
    public PmMarkType Type { get; } = type;
    public JsObject Attrs { get; } = attrs;

    /// <summary>prosemirror-model 1.25.11 Mark.toJSON.</summary>
    public JsObject ToJson()
    {
        var obj = new JsObject { ["type"] = Type.Name };
        if (Attrs.Count > 0) obj["attrs"] = Attrs;
        return obj;
    }

    /// <summary>prosemirror-model 1.25.11 Mark.eq.</summary>
    public bool Eq(PmMark other) => ReferenceEquals(this, other) || (Type == other.Type && PmNode.CompareDeep(Attrs, other.Attrs));
}

/// <summary>A ProseMirror node (a text node when <see cref="Text"/> is set), as Node.fromJSON builds it.</summary>
internal sealed class PmNode
{
    private PmNode(string typeName, JsObject attrs, List<PmNode> content, string? text, List<PmMark> marks)
    {
        TypeName = typeName;
        Attrs = attrs;
        Content = content;
        Text = text;
        Marks = marks;
    }

    public string TypeName { get; }
    public JsObject Attrs { get; }
    public List<PmNode> Content { get; }
    public string? Text { get; }
    public List<PmMark> Marks { get; }
    public bool IsText => Text is not null;

    /// <summary>
    /// prosemirror-model 1.25.11 Schema.nodeFromJSON (Node.fromJSON): node and mark types must exist; attributes computed as
    /// computeAttrs computes them (declared order, an absent one its default, undeclared ones dropped); marks sorted by rank
    /// (Mark.setFrom); adjacent text nodes with the same markup joined (Fragment.fromArray). Content is not checked (create, not createChecked).
    /// </summary>
    public static PmNode FromJson(PmSchema schema, JsonElement json) => FromJson(schema, json, 1);

    private static PmNode FromJson(PmSchema schema, JsonElement json, int depth)
    {
        if (depth > schema.MaxDepth) throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, $"nested deeper than {schema.MaxDepth} levels");
        if (json.ValueKind != JsonValueKind.Object) throw Invalid("Invalid input for Node.fromJSON");
        List<PmMark> marks = [];
        if (json.TryGetProperty("marks", out var jsonMarks) && jsonMarks.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (jsonMarks.ValueKind != JsonValueKind.Array) throw Invalid("Invalid mark data for Node.fromJSON");
            marks = [.. jsonMarks.EnumerateArray().Select(mark => MarkFromJson(schema, mark)).OrderBy(mark => mark.Type.Rank)];
        }
        var type = json.TryGetProperty("type", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "";
        if (type == "text")
        {
            if (!json.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw Invalid("Invalid text node in JSON");
            var value = text.GetString()!;
            if (value.Length == 0) throw Invalid("Empty text nodes are not allowed");
            return new PmNode("text", new JsObject(), [], value, marks);
        }
        if (!schema.Nodes.TryGetValue(type, out var nodeType))
            throw new ContentRejectedException(ContentRejectionReason.UnknownNode, $"Unknown node type: {type}");
        List<PmNode> content = [];
        if (json.TryGetProperty("content", out var jsonContent) && jsonContent.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (jsonContent.ValueKind != JsonValueKind.Array) throw Invalid("Invalid input for Fragment.fromJSON");
            content = FromArray([.. jsonContent.EnumerateArray().Select(child => FromJson(schema, child, depth + 1))]);
        }
        return new PmNode(type, ComputeAttrs(type, nodeType.Attributes, json), content, null, marks);
    }

    /// <summary>prosemirror-model 1.25.11 Mark.fromJSON.</summary>
    private static PmMark MarkFromJson(PmSchema schema, JsonElement json)
    {
        var type = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("type", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "";
        if (!schema.Marks.TryGetValue(type, out var markType))
            throw new ContentRejectedException(ContentRejectionReason.UnknownMark, $"There is no mark type {type} in this schema");
        return new PmMark(markType, ComputeAttrs(type, markType.Attributes, json));
    }

    /// <summary>prosemirror-model 1.25.11 computeAttrs (`value &amp;&amp; value[name]`, so attrs null gives every attribute null).</summary>
    private static JsObject ComputeAttrs(string owner, IReadOnlyList<PmAttribute> attributes, JsonElement json)
    {
        var built = new JsObject();
        var given = json.TryGetProperty("attrs", out var attrs) ? attrs : default;
        foreach (var attribute in attributes)
        {
            object? value;
            if (given.ValueKind == JsonValueKind.Null) value = null;
            else if (given.ValueKind == JsonValueKind.Object && TryGetLast(given, attribute.Name, out var found)) value = Js.FromJson(found);
            else if (attribute.HasDefault) value = attribute.DefaultValue;
            else throw new ContentRejectedException(ContentRejectionReason.InvalidDocument, $"{owner}: No value supplied for attribute {attribute.Name}");
            built[attribute.Name] = value;
        }
        return built;
    }

    private static bool TryGetLast(JsonElement obj, string key, out JsonElement found)
    {
        found = default;
        var any = false;
        foreach (var property in obj.EnumerateObject())
            if (property.NameEquals(key))
            {
                found = property.Value;
                any = true;
            }
        return any;
    }

    /// <summary>prosemirror-model 1.25.11 Fragment.fromArray: a text node joins the one before when it has the same markup.</summary>
    private static List<PmNode> FromArray(List<PmNode> array)
    {
        List<PmNode>? joined = null;
        for (var i = 0; i < array.Count; i++)
        {
            var node = array[i];
            if (i > 0 && node.IsText && array[i - 1].SameMarkup(node))
            {
                joined ??= array[..i];
                var last = joined[^1];
                joined[^1] = new PmNode(last.TypeName, last.Attrs, last.Content, last.Text + node.Text, last.Marks);
            }
            else joined?.Add(node);
        }
        return joined ?? array;
    }

    /// <summary>prosemirror-model 1.25.11 Node.sameMarkup (hasMarkup): type, attributes (compareDeep) and marks (Mark.sameSet).</summary>
    private bool SameMarkup(PmNode other)
        => TypeName == other.TypeName && CompareDeep(Attrs, other.Attrs)
            && (ReferenceEquals(Marks, other.Marks) || (Marks.Count == other.Marks.Count && Marks.Zip(other.Marks).All(pair => pair.First.Eq(pair.Second))));

    /// <summary>prosemirror-model 1.25.11 compareDeep.</summary>
    public static bool CompareDeep(object? a, object? b)
    {
        if (Js.StrictEquals(a, b)) return true;
        if (a is not (JsObject or JsArray or byte[]) || b is not (JsObject or JsArray or byte[])) return false;
        var array = a is JsArray;
        if (b is JsArray != array) return false;
        if (a is JsArray left && b is JsArray right) return left.Count == right.Count && left.Zip(right).All(pair => CompareDeep(pair.First, pair.Second));
        var aKeys = Js.Keys(a).ToArray();
        var bKeys = Js.Keys(b).ToHashSet(StringComparer.Ordinal);
        foreach (var p in aKeys)
            if (!bKeys.Contains(p) || !CompareDeep(Js.Get(a, p), Js.Get(b, p))) return false;
        return bKeys.All(p => aKeys.Contains(p, StringComparer.Ordinal));
    }

    private static ContentRejectedException Invalid(string detail) => new(ContentRejectionReason.InvalidStructure, detail);
}
