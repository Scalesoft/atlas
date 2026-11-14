using System.Collections.Frozen;
using System.Text.Json;

namespace Atlas.Markdown.Sync;

/// <summary>
/// What the managed Yjs content guard admits: root fragment names, node names with their attribute keys, mark names, the
/// reserved nodes a host must decide itself (<see cref="IReservedNodeGuard"/>) and, for a policy made from a
/// <see cref="MarkdownDocumentValidator"/> (<see cref="CreateDefault"/>, <see cref="FromValidator"/>), the editor's full
/// document rules: every attribute value the payload writes is checked against its constraint, and the document the
/// payload produces must be one the editor opens. Content may only sit where y-prosemirror writes it: elements under the
/// fragment or other elements, strings and marks in XmlText, attribute values as Any on elements. Every struct of a payload
/// is checked, including content the same payload deletes.
/// </summary>
public sealed class YjsContentPolicy
{
    /// <summary>The nodes a host decides by default: password references of the secrets plugin.</summary>
    public static IReadOnlyList<string> DefaultReservedNodes { get; } = ["secretReference", "secretInlineReference"];

    private readonly FrozenSet<string> _roots;
    private readonly FrozenDictionary<string, FrozenSet<string>> _nodes;
    private readonly FrozenSet<string> _marks;
    private readonly FrozenSet<string> _reserved;

    /// <summary>An allowlist only: names and attribute keys, without value or document validation.</summary>
    public YjsContentPolicy(IEnumerable<string> roots, IReadOnlyDictionary<string, IReadOnlyCollection<string>> nodes,
        IEnumerable<string> marks, IEnumerable<string> reserved)
    {
        _roots = roots.ToFrozenSet(StringComparer.Ordinal);
        _nodes = nodes.ToFrozenDictionary(node => node.Key, node => node.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal);
        _marks = marks.ToFrozenSet(StringComparer.Ordinal);
        _reserved = reserved.ToFrozenSet(StringComparer.Ordinal);
        if (_roots.Count == 0) throw new ArgumentException("At least one root fragment is required.", nameof(roots));
        foreach (var name in _reserved)
            if (!_nodes.ContainsKey(name)) throw new ArgumentException($"Reserved node '{name}' is not a declared node.", nameof(reserved));
    }

    /// <summary>Node names whose creation and attribute writes the host decides (<see cref="IReservedNodeGuard"/>).</summary>
    internal FrozenSet<string> ReservedNodes => _reserved;

    /// <summary>Root fragments the editor binds ("default").</summary>
    internal FrozenSet<string> Roots => _roots;

    /// <summary>The full document rules, or null for a bare allowlist.</summary>
    internal MarkdownDocumentValidator? Validator { get; private init; }

    /// <summary>
    /// Declared nodes no update may create: retired blocks the schema keeps only so stored and live documents that still hold them
    /// open until the content repair moves them. An old cached client inserting one is refused; snapshots and seeds may hold them.
    /// </summary>
    internal FrozenSet<string> Retired { get; private init; } = FrozenSet<string>.Empty;

    /// <summary>Throws when <paramref name="update"/> creates a <see cref="Retired"/> node.</summary>
    internal void CheckNoRetired(YjsUpdate update)
    {
        if (Retired.Count == 0) return;
        foreach (ref readonly var current in update.Structs.AsSpan())
            if (current.Content == YContentKind.Type && current.TypeRef == YTypeRef.XmlElement && Retired.Contains(current.Name!))
                throw new ContentRejectedException(ContentRejectionReason.UnknownNode, $"retired node {current.Name}");
    }

    /// <summary>The built-in editor schema (<see cref="MarkdownDocumentValidator.Builtin"/>) with the default reserved nodes.</summary>
    public static YjsContentPolicy CreateDefault() => FromValidator(MarkdownDocumentValidator.Builtin);

    /// <summary>
    /// The full rules of <paramref name="validator"/>'s schema. <paramref name="reserved"/> defaults to the
    /// <see cref="DefaultReservedNodes"/> the schema has; <paramref name="roots"/> to the editor's "default" fragment;
    /// <paramref name="retired"/> are the <see cref="Retired"/> nodes (none by default).
    /// </summary>
    public static YjsContentPolicy FromValidator(MarkdownDocumentValidator validator, IEnumerable<string>? reserved = null, IEnumerable<string>? roots = null,
        IEnumerable<string>? retired = null)
    {
        var schema = validator.Schema;
        var nodes = schema.Nodes.Values.Where(node => !node.IsText && node.Name != schema.TopNode)
            .ToDictionary(node => node.Name, node => (IReadOnlyCollection<string>)node.Attributes.Keys.ToArray(), StringComparer.Ordinal);
        var retiring = (retired ?? []).ToFrozenSet(StringComparer.Ordinal);
        if (!retiring.All(nodes.ContainsKey)) throw new ArgumentException("A retired node is not a declared node.", nameof(retired));
        return new YjsContentPolicy(roots ?? ["default"], nodes, schema.MarkNames,
            reserved ?? DefaultReservedNodes.Where(nodes.ContainsKey)) { Validator = validator, Retired = retiring };
    }

    /// <summary>
    /// Throws <see cref="ContentRejectedException"/> for the first violation of a resolved payload. Returns the reserved
    /// nodes the payload creates or whose attributes it writes; the host decides those before anything is recorded.
    /// </summary>
    internal IReadOnlyList<YReservedWrite> Check(ReadOnlySpan<byte> payload, YjsUpdate update, YParent[] parents, YjsDocumentIndex index)
    {
        Dictionary<(YParentKind Kind, int Id), YReservedWrite>? reserved = null;
        var structs = update.Structs;
        for (var item = 0; item < structs.Length; item++)
        {
            ref readonly var current = ref structs[item];
            if (current.Content == YContentKind.Gc) continue;
            CheckContent(current);
            if (current.Content == YContentKind.Type && current.TypeRef == YTypeRef.XmlElement && _reserved.Contains(current.Name!))
                Reserved(ref reserved, (YParentKind.PayloadType, item), current.Name!, isNew: true, new YId(current.Client, current.Clock));
            var parent = parents[item];
            if (parent.Kind == YParentKind.Gc) continue;
            if (parent.Kind == YParentKind.Root)
            {
                if (!_roots.Contains(parent.Root!)) throw new ContentRejectedException(ContentRejectionReason.OtherRoot, $"root '{parent.Root}'");
                if (parent.Sub is not null) throw Invalid("attribute on a root fragment");
                RequireChildContent(current, parent.Root!);
                continue;
            }
            var node = parent.Kind == YParentKind.Type
                ? index.GetTypeNode(parent.Id)
                : new YTypeNode(structs[parent.Id].TypeRef, structs[parent.Id].Name);
            if (node.Kind == YTypeRef.XmlText)
            {
                if (parent.Sub is not null) throw Invalid($"attribute '{parent.Sub}' on text");
                if (current.Content is not (YContentKind.String or YContentKind.Format or YContentKind.Deleted))
                    throw Invalid($"{current.Content} inside text");
                continue;
            }
            if (node.Kind != YTypeRef.XmlElement) throw Invalid($"content inside a {node.Kind}");
            var name = node.Name!;
            if (_reserved.Contains(name))
            {
                if (parent.Sub is null) throw new ContentRejectedException(ContentRejectionReason.ReservedNode, $"content inside {name}");
                RequireAttributeValue(current, name, parent.Sub);
                CheckValue(payload, current, name, parent.Sub);
                var write = Reserved(ref reserved, (parent.Kind, parent.Id), name, isNew: parent.Kind == YParentKind.PayloadType,
                    parent.Kind == YParentKind.PayloadType ? new YId(structs[parent.Id].Client, structs[parent.Id].Clock) : null);
                if (current.Content == YContentKind.Any)
                    write.Attributes[parent.Sub] = payload.Slice(current.ValueOffset, current.ValueLength).ToArray();
                continue;
            }
            if (parent.Sub is null)
            {
                RequireChildContent(current, name);
                continue;
            }
            if (!_nodes.TryGetValue(name, out var attributes) || !attributes.Contains(parent.Sub))
                throw new ContentRejectedException(ContentRejectionReason.UnknownAttribute, $"{name}.{parent.Sub}");
            RequireAttributeValue(current, name, parent.Sub);
            CheckValue(payload, current, name, parent.Sub);
        }
        return reserved is null ? [] : [.. reserved.Values];
    }

    /// <summary>
    /// Every attribute value the payload writes, also one it overwrites or deletes again, against its constraint. A rule that
    /// reads sibling attributes (table colwidth reads colspan) checks the value alone here; the element's validation adds the rest.
    /// </summary>
    private void CheckValue(ReadOnlySpan<byte> payload, in YStruct current, string node, string key)
    {
        if (Validator is null || current.Content != YContentKind.Any) return;
        var value = payload.Slice(current.ValueOffset, current.ValueLength);
        if (Lib0AnyJson.IsUndefined(value) || !Validator.Schema.Nodes.TryGetValue(node, out var type)
            || !type.Attributes.TryGetValue(key, out var attribute)) return;
        if (Validator.CheckAttribute(attribute, Lib0AnyJson.ToJson(value, strict: true), default) is { } reason)
            throw new ContentRejectedException(ContentRejectionReason.InvalidAttributeValue, $"{node}.{key} {reason}");
    }

    private static void RequireChildContent(in YStruct current, string parent)
    {
        if (current.Content is not (YContentKind.Type or YContentKind.Deleted)) throw Invalid($"{current.Content} directly inside {parent}");
    }

    private static void RequireAttributeValue(in YStruct current, string node, string key)
    {
        if (current.Content is not (YContentKind.Any or YContentKind.Deleted)) throw Invalid($"{current.Content} as the value of {node}.{key}");
    }

    private static ContentRejectedException Invalid(string detail) => new(ContentRejectionReason.InvalidStructure, detail);

    private void CheckContent(in YStruct current)
    {
        switch (current.Content)
        {
            case YContentKind.Json:
            case YContentKind.Binary:
            case YContentKind.Embed:
            case YContentKind.Doc:
                throw new ContentRejectedException(ContentRejectionReason.UnsupportedContent, current.Content.ToString());
            case YContentKind.Type when current.TypeRef == YTypeRef.XmlElement:
                if (!_nodes.ContainsKey(current.Name!)) throw new ContentRejectedException(ContentRejectionReason.UnknownNode, current.Name!);
                break;
            case YContentKind.Type when current.TypeRef != YTypeRef.XmlText:
                throw new ContentRejectedException(ContentRejectionReason.UnsupportedContent, $"shared type {current.TypeRef}");
            case YContentKind.Format when !_marks.Contains(YjsTreeValidation.MarkName(current.Name!)):
                throw new ContentRejectedException(ContentRejectionReason.UnknownMark, current.Name!);
        }
    }

    private static YReservedWrite Reserved(ref Dictionary<(YParentKind Kind, int Id), YReservedWrite>? reserved, (YParentKind Kind, int Id) key,
        string name, bool isNew, YId? payloadType)
    {
        reserved ??= [];
        if (!reserved.TryGetValue(key, out var write))
            reserved[key] = write = new YReservedWrite(name, isNew, key.Kind == YParentKind.Type ? key.Id : null, payloadType);
        return write;
    }
}

/// <summary>A reserved node one payload creates or whose attributes it writes; values are lib0 Any encodings (last write wins).</summary>
/// <param name="indexType">The node's shared type in the document index, when it already existed.</param>
/// <param name="payloadType">The id of the node's shared type when the payload itself carries it (new node, snapshot).</param>
internal sealed class YReservedWrite(string nodeName, bool isNew, int? indexType = null, YId? payloadType = null)
{
    public string NodeName { get; } = nodeName;
    public bool IsNew { get; } = isNew;
    public int? IndexType { get; } = indexType;
    public YId? PayloadType { get; } = payloadType;
    public Dictionary<string, byte[]> Attributes { get; } = new(StringComparer.Ordinal);
}
