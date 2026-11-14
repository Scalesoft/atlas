namespace Atlas.Markdown.Sync;

/// <summary>A run of clocks [Start, End) of one client whose structs share one parent.</summary>
internal readonly record struct YIndexSegment(uint Start, uint End, YParent Parent);

/// <summary>A shared type known to a document: its kind and, for an XmlElement, the ProseMirror node name.</summary>
internal readonly record struct YTypeNode(YTypeRef Kind, string? Name);

/// <summary>
/// What the guard knows about one document: the state vector and, for every clock, the parent its struct integrated
/// under (consecutive structs of a client with the same parent share one segment), plus kind and node name of every
/// live shared type. That resolves the parent and attribute key of anything a later update links to. Content and
/// deletions are not stored, so memory stays a fraction of the document state. Built from trusted, previously
/// admitted state and changed only by <see cref="Commit"/> after a payload passed every check; a rejected payload
/// leaves it untouched. Not thread-safe: callers hold the per-document gate.
/// For the node names it tracks (the reserved nodes), it also keeps each live node's last admitted attribute values,
/// so a host can compare an attribute rewrite with the value it replaces.
/// </summary>
/// <param name="tree">Also keep the document itself (<see cref="Tree"/>), for the document validation of the guard.</param>
/// <param name="text">The tree also keeps the characters (<see cref="YjsDocumentMaterializer"/>).</param>
internal sealed class YjsDocumentIndex(IReadOnlySet<string>? trackedNodes = null, bool tree = false, bool text = false)
{
    private static readonly IReadOnlyDictionary<string, byte[]> NoAttributes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    private readonly Dictionary<int, Dictionary<string, byte[]>> _attributes = [];
    private readonly Dictionary<ulong, uint> _stateVector = [];
    private readonly Dictionary<ulong, List<YIndexSegment>> _segments = [];
    private readonly Dictionary<YId, int> _typeIds = [];
    private readonly List<YTypeNode> _types = [];
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<ulong, uint> StateVector => _stateVector;

    /// <summary>The admitted document, when the index keeps it. The guard applies payloads to it (journaled) before <see cref="Commit"/>.</summary>
    public YjsDocumentTree? Tree { get; } = tree ? new YjsDocumentTree(text) : null;
    public bool IsEmpty => _stateVector.Count == 0;
    public int SegmentCount { get; private set; }
    public int TypeCount => _types.Count;

    /// <summary>Indexes trusted state: a stored full state (may be empty) and the stored log above it. Structural checks only.</summary>
    public static YjsDocumentIndex Build(ReadOnlyMemory<byte> baseline, IEnumerable<ReadOnlyMemory<byte>> log, long maxBytes,
        IReadOnlySet<string>? trackedNodes = null, bool tree = false, bool text = false)
    {
        var index = new YjsDocumentIndex(trackedNodes, tree, text);
        if (!baseline.IsEmpty) index.ApplyTrusted(baseline.Span, maxBytes);
        foreach (var update in log) index.ApplyTrusted(update.Span, maxBytes);
        return index;
    }

    /// <summary>Integrates previously admitted content without the content policy; it must still be well formed and resolvable.</summary>
    public void ApplyTrusted(ReadOnlySpan<byte> payload, long maxBytes)
    {
        var update = YjsUpdateDecoder.Decode(payload, maxBytes);
        var parents = YjsParentResolver.Resolve(update, this);
        Tree?.Apply(payload, update, journal: false, bounded: false);
        Commit(payload, update, parents);
    }

    public uint GetClock(ulong client) => _stateVector.GetValueOrDefault(client);

    public YTypeNode GetTypeNode(int typeId) => _types[typeId];

    /// <summary>The shared type a payload struct id created, if the index knows it.</summary>
    public bool TryGetTypeId(YId id, out int typeId) => _typeIds.TryGetValue(id, out typeId);

    /// <summary>Last admitted attribute values (lib0 Any) of a tracked node's type; empty for any other type.</summary>
    public IReadOnlyDictionary<string, byte[]> GetAttributes(int typeId)
        => _attributes.TryGetValue(typeId, out var attributes) ? attributes : NoAttributes;

    /// <summary>Finds the struct holding a clock: the parent it integrated under and, if it created a live shared type, its type id (else -1).</summary>
    public bool TryLocate(YId id, out YParent parent, out int typeId)
    {
        parent = YParent.Gc;
        typeId = -1;
        if (id.Clock >= GetClock(id.Client)) return false;
        // Commits only append from the current clock on, so a client's segments cover [0, clock) in order.
        var segments = _segments[id.Client];
        var low = 0;
        var high = segments.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (segments[middle].Start <= id.Clock) low = middle;
            else high = middle - 1;
        }
        parent = segments[low].Parent;
        if (_typeIds.TryGetValue(id, out var found)) typeId = found;
        return true;
    }

    /// <summary>
    /// Records a resolved payload. Throws <see cref="ContentRejectedException"/> only before the first mutation
    /// (capacity), so a payload is recorded completely or not at all.
    /// </summary>
    public void Commit(ReadOnlySpan<byte> payload, YjsUpdate update, YParent[] parents)
    {
        var structs = update.Structs;
        var newTypes = 0;
        for (var item = 0; item < structs.Length; item++)
            if (IsNewType(structs[item], parents[item])) newTypes++;
        if (SegmentCount + structs.Length > YjsLimits.MaxIndexSegments || _types.Count + newTypes > YjsLimits.MaxIndexTypes)
            throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, "document index is full");

        // Types first: a struct may name a parent type that a later client list of the same payload creates.
        Dictionary<int, int>? payloadTypes = null;
        for (var item = 0; item < structs.Length; item++)
        {
            ref readonly var current = ref structs[item];
            if (!IsNewType(current, parents[item])) continue;
            payloadTypes ??= [];
            payloadTypes[item] = _types.Count;
            _typeIds[new YId(current.Client, current.Clock)] = _types.Count;
            _types.Add(new YTypeNode(current.TypeRef, Intern(current.Name)));
        }
        foreach (var range in update.Clients)
        {
            var known = GetClock(range.Client);
            if (range.Count == 0 || range.End <= known) continue;
            if (!_segments.TryGetValue(range.Client, out var segments)) _segments[range.Client] = segments = [];
            for (var item = range.First; item < range.First + range.Count; item++)
            {
                ref readonly var current = ref structs[item];
                var start = Math.Max(current.Clock, known);
                if (current.End <= start) continue;
                var parent = parents[item];
                if (parent.Kind == YParentKind.PayloadType) parent = parent with { Kind = YParentKind.Type, Id = payloadTypes![parent.Id] };
                Append(segments, start, current.End, parent with { Root = Intern(parent.Root), Sub = Intern(parent.Sub) });
                if (start == current.Clock) Track(payload, current, parent);
            }
            _stateVector[range.Client] = range.End;
        }
    }

    /// <summary>Same state vector, segments and types (proves that incremental admission equals a rebuild).</summary>
    public bool IsEquivalentTo(YjsDocumentIndex other)
    {
        if (_stateVector.Count != other._stateVector.Count || _segments.Count != other._segments.Count ||
            _typeIds.Count != other._typeIds.Count || !_types.SequenceEqual(other._types)) return false;
        foreach (var (client, clock) in _stateVector)
            if (other.GetClock(client) != clock) return false;
        foreach (var (client, segments) in _segments)
            if (!other._segments.TryGetValue(client, out var theirs) || !segments.SequenceEqual(theirs)) return false;
        foreach (var (id, typeId) in _typeIds)
            if (!other._typeIds.TryGetValue(id, out var theirs) || theirs != typeId) return false;
        if (_attributes.Count != other._attributes.Count) return false;
        foreach (var (typeId, attributes) in _attributes)
        {
            if (!other._attributes.TryGetValue(typeId, out var theirs) || attributes.Count != theirs.Count) return false;
            foreach (var (key, value) in attributes)
                if (!theirs.TryGetValue(key, out var their) || !value.AsSpan().SequenceEqual(their)) return false;
        }
        return true;
    }

    /// <summary>An attribute value written on a tracked node: remember it as that node's current value (last write wins).</summary>
    private void Track(ReadOnlySpan<byte> payload, in YStruct current, YParent parent)
    {
        if (trackedNodes is null || parent.Kind != YParentKind.Type || parent.Sub is null || current.Content != YContentKind.Any) return;
        var node = _types[parent.Id];
        if (node.Kind != YTypeRef.XmlElement || node.Name is null || !trackedNodes.Contains(node.Name)) return;
        if (!_attributes.TryGetValue(parent.Id, out var attributes)) _attributes[parent.Id] = attributes = new(StringComparer.Ordinal);
        attributes[Intern(parent.Sub)!] = payload.Slice(current.ValueOffset, current.ValueLength).ToArray();
    }

    // A type Yjs garbage-collects on integration is no parent for anything; re-sent types are already known.
    private bool IsNewType(in YStruct current, YParent parent)
        => current.Content == YContentKind.Type && parent.Kind != YParentKind.Gc && current.Clock >= GetClock(current.Client);

    private void Append(List<YIndexSegment> segments, uint start, uint end, YParent parent)
    {
        if (segments.Count > 0)
        {
            var last = segments[^1];
            if (last.End == start && last.Parent == parent)
            {
                segments[^1] = last with { End = end };
                return;
            }
        }
        segments.Add(new YIndexSegment(start, end, parent));
        SegmentCount++;
    }

    private string? Intern(string? name)
    {
        if (name is null) return null;
        if (_names.TryGetValue(name, out var existing)) return existing;
        _names[name] = name;
        return name;
    }
}
