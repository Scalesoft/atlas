using System.Buffers;
using System.Text.Json;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Validates the ProseMirror document a <see cref="YjsDocumentTree"/> holds with <see cref="MarkdownDocumentValidator"/>, as
/// y-prosemirror reads it: an element is a node with its visible attributes (an undefined value is absent), an XmlText is a
/// run of text nodes whose marks are the formats in force (y-prosemirror's "name--hash" keys name the mark), and the root
/// fragment is the top node. Each element is validated on its own with its children reduced to their type (validated as
/// elements of their own), so an update costs the size of the elements it changed, not of the document.
/// </summary>
internal static class YjsTreeValidation
{
    [ThreadStatic] private static long t_itemsRead;

    /// <summary>
    /// The replica items validation has read on this thread so far: the cost of validating an update as a count, which unlike
    /// its wall-clock time does not depend on the machine (YjsRobustnessTests bounds it per update).
    /// </summary>
    public static long ItemsRead => t_itemsRead;

    /// <summary>
    /// The first violation among the elements an update changed (and the tables around changed rows or cells), or null. Every
    /// element may hold what Yjs merged from concurrent edits (MarkdownDocumentValidator.ValidateNode, merged).
    /// </summary>
    public static string? ValidateChanged(IReadOnlySet<YTreeType> changed, MarkdownDocumentValidator validator, IReadOnlySet<string> required)
    {
        var tables = new HashSet<YTreeType>();
        foreach (var type in changed)
        {
            if (Placement(type) is not { } depth) continue; // inside deleted content: invisible
            if (depth < 0) return $"{type.Name}: is nested deeper than {MarkdownDocumentValidator.MaxDepth} levels (too_deep)";
            if (Validate(type, depth, validator, required) is { } violation) return violation;
            if (type.Name is "tableRow" or "tableCell" or "tableHeader" && Table(type) is { } table) tables.Add(table);
        }
        foreach (var table in tables)
        {
            if (changed.Contains(table) || Placement(table) is not { } depth) continue;
            if (Validate(table, depth, validator, required) is { } violation) return violation;
        }
        return null;
    }

    /// <summary>
    /// The whole document of a snapshot or seed, as one JSON document. Elements deeper than the validator's node bound are cut
    /// off one level below it, where the walk refuses them. A snapshot or seed is an editor's merged document.
    /// </summary>
    public static string? ValidateAll(YjsDocumentTree tree, IEnumerable<string> roots, MarkdownDocumentValidator validator, IReadOnlySet<string> required)
    {
        foreach (var name in roots)
        {
            if (tree.Root(name) is not { } root || !Children(root).Any()) continue;
            var buffer = new ArrayBufferWriter<byte>();
            try
            {
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = DeepJsonDepth, SkipValidation = true }))
                    Element(writer, root, validator.Schema.TopNode, Mode.Deep, MarkdownDocumentValidator.MaxDepth);
                using var document = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = DeepJsonDepth });
                if (validator.ValidateNode(document.RootElement, 1, required, top: true, merged: true) is { } violation) return $"{validator.Schema.TopNode}: {violation}";
            }
            catch (ContentRejectedException rejection)
            {
                return rejection.Detail;
            }
        }
        return null;
    }

    /// <summary>Two JSON levels per node within the bound and one below it, plus the deepest attribute value (lib0 nesting) and its attrs object.</summary>
    private const int DeepJsonDepth = 2 * (MarkdownDocumentValidator.MaxDepth + 1) + YjsLimits.MaxValueDepth + 8;

    /// <summary>The node depth of a visible element (the root is 1), null when it or an ancestor is deleted, -1 when too deep.</summary>
    private static int? Placement(YTreeType type)
    {
        var depth = 1;
        for (var current = type; !current.IsRoot; current = current.Item!.Parent!)
        {
            if (current.Item!.Deleted) return null;
            if (++depth > MarkdownDocumentValidator.MaxDepth) return -1;
        }
        return depth;
    }

    private static YTreeType? Table(YTreeType type)
    {
        for (var current = type; !current.IsRoot; current = current.Item!.Parent!)
            if (current.Name == "table") return current;
        return null;
    }

    private static string? Validate(YTreeType type, int depth, MarkdownDocumentValidator validator, IReadOnlySet<string> required)
    {
        // An empty fragment is a document y-prosemirror fills with the top node's default content (an empty paragraph).
        if (type.IsRoot && !Children(type).Any()) return null;
        // A container of elements only (the document, lists, rows) passes its children as names, without JSON.
        var names = type.Name != "table" && Children(type).All(child => child.Kind == YTypeRef.XmlElement)
            ? Children(type).Select(child => child.Name!).ToArray() : null;
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
                Element(writer, type, validator.Schema.TopNode, names is null ? Mode.Full : Mode.WithoutContent);
            using var document = JsonDocument.Parse(buffer.WrittenMemory, MarkdownDocumentValidator.JsonOptions);
            return validator.ValidateNode(document.RootElement, depth, required, top: type.IsRoot, names, merged: true) is { } violation
                ? $"{(type.IsRoot ? validator.Schema.TopNode : type.Name)}: {violation}"
                : null;
        }
        catch (ContentRejectedException rejection)
        {
            return $"{type.Name}: {rejection.Detail}";
        }
    }

    private static IEnumerable<YTreeType> Children(YTreeType type)
    {
        for (var item = type.Start; item is not null; item = item.Right)
        {
            t_itemsRead++;
            if (!item.Deleted && item.Type is { } child) yield return child;
        }
    }

    /// <summary>
    /// How much of an element the JSON holds: its whole subtree, everything but its children's content, all but its content,
    /// its type, a table row's cells, a cell's attributes.
    /// </summary>
    private enum Mode { Deep, Full, WithoutContent, Shallow, TableRow, TableCell }

    /// <summary>An element with its children reduced to their type (a table's rows keep their cells' attributes for the geometry).</summary>
    /// <param name="levels">For <see cref="Mode.Deep"/>: the levels of children still written.</param>
    private static void Element(Utf8JsonWriter writer, YTreeType type, string topNode, Mode mode, int levels = 0)
    {
        if (mode == Mode.Shallow)
        {
            writer.WriteStringValue(type.Name);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("type", type.IsRoot ? topNode : type.Name);
        if (mode is Mode.TableRow or Mode.TableCell) writer.WriteBoolean(MarkdownDocumentValidator.ShallowMarker, true);
        if (mode is Mode.Deep or Mode.Full or Mode.WithoutContent or Mode.TableCell)
        {
            var attributes = type.Map?.Where(pair => !pair.Value.Deleted && pair.Value.Content == YContentKind.Any && pair.Value.Value is { } value
                && !Lib0AnyJson.IsUndefined(value)).ToArray() ?? [];
            if (attributes.Length > 0)
            {
                writer.WriteStartObject("attrs");
                foreach (var (key, item) in attributes)
                {
                    writer.WritePropertyName(key);
                    Lib0AnyJson.Write(item.Value, writer, strict: true);
                }
                writer.WriteEndObject();
            }
        }
        if (mode is Mode.Full or Mode.TableRow || (mode == Mode.Deep && levels >= 0))
        {
            writer.WriteStartArray("content");
            foreach (var child in Children(type))
            {
                if (child.Kind == YTypeRef.XmlText)
                {
                    if (mode is Mode.Full or Mode.Deep) Text(writer, child);
                }
                else if (child.Kind == YTypeRef.XmlElement)
                    Element(writer, child, topNode, mode switch
                    {
                        Mode.Deep => Mode.Deep,
                        Mode.TableRow => Mode.TableCell,
                        _ => type.Name == "table" ? Mode.TableRow : Mode.Shallow,
                    }, levels - 1);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    /// <summary>Y.Text.toDelta: one text node per string run with the formats in force (a null format ends one).</summary>
    private static void Text(Utf8JsonWriter writer, YTreeType text)
    {
        var formats = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var item = text.Start; item is not null; item = item.Right)
        {
            t_itemsRead++;
            if (item.Deleted) continue;
            if (item.Content == YContentKind.Format)
            {
                if (item.Value is [(byte)'n', (byte)'u', (byte)'l', (byte)'l']) formats.Remove(item.Key!);
                else formats[item.Key!] = item.Value!;
                continue;
            }
            if (item.Content != YContentKind.String) continue;
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            // The characters do not matter to any rule; a text node only has to be non-empty.
            writer.WriteString("text", "x");
            if (formats.Count > 0)
            {
                writer.WriteStartArray("marks");
                foreach (var (key, value) in formats)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", MarkName(key));
                    writer.WritePropertyName("attrs");
                    writer.WriteRawValue(value, skipInputValidation: true);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
    }

    /// <summary>y-prosemirror's yattr2markname: overlapping marks are stored as "name--" and an eight-character hash.</summary>
    public static string MarkName(string key)
    {
        if (key.Length < 11 || key[^10] != '-' || key[^9] != '-') return key;
        foreach (var c in key.AsSpan(key.Length - 8))
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')) return key;
        return key[..^10];
    }
}
