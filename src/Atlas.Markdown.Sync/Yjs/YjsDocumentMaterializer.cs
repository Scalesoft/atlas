using System.Text;
using System.Text.Json;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Reconstructs, without a browser or JavaScript, the ProseMirror document the editor shows for a Yjs document: the stored full
/// state and the updates after it are integrated by the content guard's managed replica (<see cref="YjsDocumentTree"/>), and the
/// editor's fragment is written exactly as JSON.stringify writes the editor's getJSON(), byte for byte. That is y-tiptap's
/// conversion (an element is a node with the schema's attributes in declaration order, an absent one taking its default; an
/// XmlText's delta is text nodes whose marks are the formats in force sorted by rank; adjacent text nodes with equal marks join)
/// and ProseMirror's toJSON() key order; an empty fragment is the editor's empty document.
/// What the editor cannot show or never writes is refused with <see cref="YjsMaterializationException"/>, never returned in part:
/// a malformed or incomplete payload, an unknown node or mark, content the schema does not accept (y-tiptap would delete it),
/// embeds and other shared types, attribute values that are no plain JSON data. Immutable and thread-safe.
/// </summary>
/// <remarks>
/// Materializing costs a decoding of every payload; a 4 MiB document takes a few hundred milliseconds
/// (YjsMaterializerCorpusTests reports it).
/// </remarks>
public sealed class YjsDocumentMaterializer
{
    private static readonly Lazy<YjsDocumentMaterializer> BuiltinMaterializer = new(() => new YjsDocumentMaterializer(MarkdownSchemaManifest.Builtin));
    private readonly MarkdownSchemaManifest _schema;
    private readonly MarkdownDocumentValidator _validator;
    private readonly string _root;
    private readonly long _maxPayloadBytes;
    /// <summary>The JSON of every attribute default, written once.</summary>
    private readonly Dictionary<SchemaAttribute, string> _defaults = new(ReferenceEqualityComparer.Instance);

    /// <param name="schema">The editor configuration's schema: <see cref="MarkdownSchemaManifest.Builtin"/>, narrowed with WithPlugins or parsed.</param>
    /// <param name="root">The fragment the editor binds (the Collaboration extension's field).</param>
    /// <param name="maxPayloadBytes">The bound of each payload, the host's <see cref="MarkdownSyncOptions.MaxDocumentBytes"/>.</param>
    /// <param name="validators">Implementations of the structured validators third-party plugins name, as for <see cref="MarkdownDocumentValidator"/>.</param>
    public YjsDocumentMaterializer(MarkdownSchemaManifest schema, string root = "default", long maxPayloadBytes = 4L * 1024 * 1024,
        IReadOnlyDictionary<string, MarkdownValueValidator>? validators = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        _schema = schema;
        _validator = new MarkdownDocumentValidator(schema, validators);
        _root = root;
        _maxPayloadBytes = maxPayloadBytes;
        foreach (var attribute in schema.Nodes.Values.SelectMany(node => node.Declared).Concat(schema.Marks.Values.SelectMany(mark => mark.Declared)))
        {
            if (attribute.Default is not { } fallback) continue;
            var json = new StringBuilder();
            JsJson.Value(json, fallback);
            _defaults[attribute] = json.ToString();
        }
    }

    /// <summary>The materializer of the built-in schema (every built-in plugin) and the "default" fragment.</summary>
    public static YjsDocumentMaterializer Builtin => BuiltinMaterializer.Value;

    /// <summary>The editor's document after a stored state and the updates stored after it, as JSON text.</summary>
    /// <param name="state">A full state (Y.encodeStateAsUpdate); empty for a document nothing was written to.</param>
    /// <param name="updates">The updates after the state, in the order they were admitted.</param>
    /// <exception cref="YjsMaterializationException">A payload is malformed or incomplete, or the document holds what the editor cannot show.</exception>
    public string Materialize(ReadOnlyMemory<byte> state, IEnumerable<ReadOnlyMemory<byte>>? updates = null)
    {
        YjsDocumentIndex index;
        try
        {
            index = YjsDocumentIndex.Build(state, updates ?? [], _maxPayloadBytes, tree: true, text: true);
        }
        catch (ContentRejectedException rejection)
        {
            throw new YjsMaterializationException(rejection.Reason, rejection.Detail);
        }
        return Materialize(index.Tree!);
    }

    /// <summary>The document a tree that keeps text holds now.</summary>
    internal string Materialize(YjsDocumentTree tree)
    {
        if (!tree.KeepsText) throw new InvalidOperationException("The tree does not keep the text.");
        try
        {
            var output = new StringBuilder();
            var top = _schema.Nodes[_schema.TopNode];
            if (tree.Root(_root) is { } root && HasContent(root)) Element(output, root, top, 1);
            else Fill(output, top, 1);
            return output.ToString();
        }
        catch (ContentRejectedException rejection)
        {
            throw new YjsMaterializationException(rejection.Reason, rejection.Detail);
        }
    }

    /// <summary>
    /// createNodeFromYElement and schema.node: the children (an XmlText as the text nodes of its delta), the schema's attributes,
    /// adjacent text nodes with equal marks joined (Fragment.fromArray), then the content checked as createChecked checks it
    /// (y-tiptap deletes an element that fails; it is refused here). The root is the top node, with its default attributes.
    /// </summary>
    private void Element(StringBuilder output, YTreeType type, SchemaNode node, int depth)
    {
        if (depth > MarkdownDocumentValidator.MaxDepth)
            throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, $"{node.Name}: nested deeper than {MarkdownDocumentValidator.MaxDepth} levels");
        output.Append("{\"type\":");
        JsJson.String(output, node.Name);
        Attributes(output, node.Name, node.Declared, attribute => ElementValue(type, node, attribute));
        var children = new List<string>();
        TextRun? pending = null;
        foreach (var child in Children(type))
        {
            if (child.Kind == YTypeRef.XmlText)
            {
                foreach (var run in Runs(child))
                {
                    if (run.Types.FirstOrDefault(mark => !node.AllowedMarks.Contains(mark)) is { } refused)
                        throw new ContentRejectedException(ContentRejectionReason.InvalidDocument, $"{node.Name}: does not allow the mark {refused}");
                    if (pending?.Key == run.Key) pending.Text.Append(run.Text);
                    else
                    {
                        Flush();
                        pending = run;
                    }
                }
                continue;
            }
            Flush();
            if (child.Name is not { } name || !_schema.Nodes.TryGetValue(name, out var childNode))
                throw new ContentRejectedException(ContentRejectionReason.UnknownNode, $"{node.Name}: unknown node {child.Name}");
            if (childNode.IsText) throw new ContentRejectedException(ContentRejectionReason.InvalidStructure, $"{node.Name}: an element named {name}");
            Open(name);
            Element(output, child, childNode, depth + 1);
        }
        Flush();
        output.Append(children.Count > 0 ? "]}" : "}");
        if (!node.Accepts(children))
            throw new ContentRejectedException(ContentRejectionReason.InvalidDocument, $"{node.Name}: does not accept {string.Join(' ', children)}");

        void Open(string name)
        {
            output.Append(children.Count == 0 ? ",\"content\":[" : ",");
            children.Add(name);
        }

        void Flush()
        {
            if (pending is null) return;
            Open("text");
            output.Append("{\"type\":\"text\"");
            if (pending.Marks.Length > 0) output.Append(",\"marks\":").Append(pending.Marks);
            output.Append(",\"text\":");
            JsJson.String(output, pending.Text.ToString());
            output.Append('}');
            pending = null;
        }
    }

    /// <summary>
    /// The visible children y-tiptap builds nodes from (typeListToArray: countable items, so formats are skipped): elements and,
    /// below the root, texts. Anything else makes y-tiptap fail.
    /// </summary>
    private static IEnumerable<YTreeType> Children(YTreeType type)
    {
        for (var item = type.Start; item is not null; item = item.Right)
        {
            if (item.Deleted || item.Content == YContentKind.Format) continue;
            if (item.Type is { Kind: YTypeRef.XmlElement } element) yield return element;
            else if (item.Type is { Kind: YTypeRef.XmlText } text)
                yield return type.IsRoot ? throw new ContentRejectedException(ContentRejectionReason.InvalidStructure, "a text in the root fragment") : text;
            else throw new ContentRejectedException(ContentRejectionReason.UnsupportedContent,
                $"{(type.IsRoot ? "the root fragment" : type.Name)}: holds {(item.Type is { } other ? $"a shared type {other.Kind}" : $"{item.Content} content")}");
        }
    }

    private static bool HasContent(YTreeType root)
    {
        for (var item = root.Start; item is not null; item = item.Right)
            if (!item.Deleted && item.Content != YContentKind.Format) return true;
        return false;
    }

    /// <summary>The current value of an element attribute (lib0 Any), or null when it is absent (unset, deleted or undefined).</summary>
    private static byte[]? ElementValue(YTreeType type, SchemaNode node, SchemaAttribute attribute)
    {
        if (type.IsRoot || type.Map?.GetValueOrDefault(attribute.Name) is not { Deleted: false } item) return null;
        if (item.Content != YContentKind.Any)
            throw new ContentRejectedException(ContentRejectionReason.UnsupportedContent, $"{node.Name}: the value of {attribute.Name} is {item.Content} content");
        return Lib0AnyJson.IsUndefined(item.Value) ? null : item.Value;
    }

    /// <summary>
    /// computeAttrs: every declared attribute in order, its value or its default; toJSON() writes attrs only for a type that has
    /// attributes. A value is lib0 Any (bytes) or JSON (a mark's format value).
    /// </summary>
    private void Attributes<TValue>(StringBuilder output, string owner, IReadOnlyList<SchemaAttribute> declared, Func<SchemaAttribute, TValue?> value,
        bool sorted = false)
    {
        if (declared.Count == 0) return;
        output.Append(",\"attrs\":{");
        for (var index = 0; index < declared.Count; index++)
        {
            var attribute = declared[index];
            if (index > 0) output.Append(',');
            JsJson.String(output, attribute.Name);
            output.Append(':');
            switch (value(attribute))
            {
                case byte[] any:
                    var start = output.Length;
                    JsJson.Any(output, any);
                    if (attribute.Validated && !sorted) Validate(owner, attribute, output.ToString(start, output.Length - start));
                    break;
                case JsonElement json:
                    JsJson.Value(output, json, sorted);
                    if (attribute.Validated && !sorted) Validate(owner, attribute, json);
                    break;
                default:
                    output.Append(_defaults.GetValueOrDefault(attribute)
                        ?? throw new ContentRejectedException(ContentRejectionReason.InvalidDocument, $"{owner}: no value for the required attribute {attribute.Name}"));
                    break;
            }
        }
        output.Append('}');
    }

    /// <summary>
    /// The validator an attribute declares, which ProseMirror runs on every explicit value when it creates the node or mark (an absent
    /// attribute takes its default unchecked): y-tiptap cannot build the element then and deletes it, so it is refused here. The
    /// validator sees the value alone, not the sibling attributes.
    /// </summary>
    private void Validate(string owner, SchemaAttribute attribute, string json)
    {
        using var document = JsonDocument.Parse(json);
        Validate(owner, attribute, document.RootElement);
    }

    private void Validate(string owner, SchemaAttribute attribute, JsonElement value)
    {
        if (_validator.CheckAttribute(attribute, value, default) is { } reason)
            throw new ContentRejectedException(ContentRejectionReason.InvalidAttributeValue, $"{owner}: {attribute.Name} {reason}");
    }

    /// <summary>
    /// Y.Text.toDelta and attributesToMarks: one text node per run of characters between visible formats, its marks the formats in
    /// force (a null value ends one; y-prosemirror's "name--hash" keys name the mark) sorted by rank. toDelta skips other content
    /// but embeds and shared types, from which ProseMirror builds a broken text node.
    /// </summary>
    private IEnumerable<TextRun> Runs(YTreeType text)
    {
        var formats = new List<(string Key, JsonElement Value)>();
        (string Json, string Key, string[] Types)? marks = null;
        var run = new StringBuilder();
        for (var item = text.Start; item is not null; item = item.Right)
        {
            if (item.Deleted) continue;
            switch (item.Content)
            {
                case YContentKind.String:
                    run.Append(item.Text);
                    break;
                case YContentKind.Format:
                    if (run.Length > 0)
                    {
                        marks ??= Marks(formats);
                        yield return new TextRun(run, marks.Value.Json, marks.Value.Key, marks.Value.Types);
                        run = new StringBuilder();
                    }
                    var value = Parse(item.Value!);
                    var index = formats.FindIndex(format => format.Key == item.Key);
                    // A Map keeps the place of a key it already has.
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        if (index >= 0) formats.RemoveAt(index);
                    }
                    else if (index >= 0) formats[index] = (item.Key!, value);
                    else formats.Add((item.Key!, value));
                    marks = null;
                    break;
                case YContentKind.Type or YContentKind.Embed:
                    throw new ContentRejectedException(ContentRejectionReason.UnsupportedContent, "an embed in a text");
            }
        }
        if (run.Length > 0)
        {
            marks ??= Marks(formats);
            yield return new TextRun(run, marks.Value.Json, marks.Value.Key, marks.Value.Types);
        }
    }

    /// <summary>The marks of the formats in force: their JSON, a canonical form to compare them, their types.</summary>
    private (string Json, string Key, string[] Types) Marks(List<(string Key, JsonElement Value)> formats)
    {
        if (formats.Count == 0) return ("", "", []);
        var marks = formats.Select(format =>
            {
                // MarkName strips the hash as y-prosemirror's yattr2markname does for keys without line terminators.
                if (format.Key.AsSpan().IndexOfAny("\n\r\u2028\u2029") >= 0 || !_schema.Marks.TryGetValue(YjsTreeValidation.MarkName(format.Key), out var mark))
                    throw new ContentRejectedException(ContentRejectionReason.UnknownMark, $"unknown mark {format.Key}");
                if (mark.Declared.Count > 0 && format.Value.ValueKind != JsonValueKind.Object)
                    throw new ContentRejectedException(ContentRejectionReason.InvalidAttributeValue, $"{mark.Name}: the attributes are no object");
                return (mark, format.Value);
            })
            .OrderBy(pair => pair.mark.Rank) // Mark.setFrom: a stable sort by rank
            .ToArray();
        var json = new StringBuilder("[");
        foreach (var (mark, value) in marks)
        {
            if (json.Length > 1) json.Append(',');
            json.Append("{\"type\":");
            JsJson.String(json, mark.Name);
            Attributes(json, mark.Name, mark.Declared, attribute => JsJson.TryGetLast(value, attribute.Name, out var found) ? found : (JsonElement?)null);
            json.Append('}');
        }
        json.Append(']');
        // Attribute values ProseMirror's compareDeep finds equal may differ in the key order of nested objects only.
        var key = new StringBuilder();
        foreach (var (mark, value) in marks)
        {
            key.Append(mark.Name).Append('(');
            Attributes(key, mark.Name, mark.Declared, attribute => JsJson.TryGetLast(value, attribute.Name, out var found) ? found : (JsonElement?)null, sorted: true);
            key.Append(')');
        }
        return (json.ToString(), key.ToString(), marks.Select(pair => pair.mark.Name).ToArray());
    }

    /// <summary>
    /// createAndFill of a node without content: ProseMirror fills the content its expression requires with the first node type, in
    /// schema order, that needs no attribute. shortcut: only for an expression of one repeated set ("block+"), which is what the
    /// built-in top node and its paragraph use; another required content is refused.
    /// </summary>
    private void Fill(StringBuilder output, SchemaNode node, int depth)
    {
        output.Append("{\"type\":");
        JsJson.String(output, node.Name);
        Attributes<byte[]>(output, node.Name, node.Declared, _ => null);
        if (!node.Accepts([]))
        {
            var filler = node.Repeat is { Minimum: > 0 } repeat && depth < MarkdownDocumentValidator.MaxDepth
                ? _schema.NodeOrder.Select(name => _schema.Nodes[name]).FirstOrDefault(candidate => repeat.Set.Contains(candidate.Name) && !candidate.IsText
                    && candidate.Declared.All(_defaults.ContainsKey))
                : null;
            if (filler is null) throw new ContentRejectedException(ContentRejectionReason.InvalidDocument, $"{node.Name}: cannot fill its required content");
            output.Append(",\"content\":[");
            Fill(output, filler, depth + 1);
            output.Append(']');
        }
        output.Append('}');
    }

    private static JsonElement Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <param name="Marks">The marks' JSON array, empty without marks.</param>
    /// <param name="Key">Equal for runs whose marks ProseMirror finds equal (Node.sameMarkup).</param>
    private sealed record TextRun(StringBuilder Text, string Marks, string Key, string[] Types);
}

/// <summary>
/// A Yjs document <see cref="YjsDocumentMaterializer"/> refuses: a payload that is malformed, exceeds a bound or misses an earlier
/// one, or a document holding what the editor cannot show or never writes. <see cref="Reason"/> says which, as the content guard
/// names it.
/// </summary>
public sealed class YjsMaterializationException(ContentRejectionReason reason, string detail) : Exception($"{reason}: {detail}")
{
    public ContentRejectionReason Reason { get; } = reason;

    public string Detail { get; } = detail;
}
