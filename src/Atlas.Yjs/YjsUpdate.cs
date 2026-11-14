namespace Atlas.Yjs;

/// <summary>A Yjs struct identifier: the creating client and the logical clock of its first unit.</summary>
internal readonly record struct YId(ulong Client, uint Clock);

/// <summary>The lib0 v1 content reference of a struct (the low five bits of its info byte).</summary>
internal enum YContentKind : byte
{
    Gc = 0,
    Deleted = 1,
    Json = 2,
    Binary = 3,
    String = 4,
    Embed = 5,
    Format = 6,
    Type = 7,
    Any = 8,
    Doc = 9,
}

/// <summary>The Yjs shared type a <see cref="YContentKind.Type"/> struct creates.</summary>
internal enum YTypeRef : byte
{
    Array = 0,
    Map = 1,
    Text = 2,
    XmlElement = 3,
    XmlFragment = 4,
    XmlHook = 5,
    XmlText = 6,
}

/// <summary>
/// One decoded struct. A GC struct only has an id, a length and <see cref="YContentKind.Gc"/>. An item has an origin
/// and/or a right origin (and inherits parent and key from them) or explicit parent info.
/// </summary>
internal readonly record struct YStruct
{
    public required ulong Client { get; init; }
    public required uint Clock { get; init; }
    public required uint Length { get; init; }
    public required YContentKind Content { get; init; }
    public YId? Origin { get; init; }
    public YId? RightOrigin { get; init; }
    /// <summary>Explicit parent given as the id of the struct that created the parent type.</summary>
    public YId? ParentId { get; init; }
    /// <summary>Explicit parent given as a root type name.</summary>
    public string? ParentRoot { get; init; }
    /// <summary>Explicit map key (element attribute name).</summary>
    public string? ParentSub { get; init; }
    public YTypeRef TypeRef { get; init; }
    /// <summary>The node name of an XmlElement or XmlHook type, or the key of a format.</summary>
    public string? Name { get; init; }
    /// <summary>
    /// Payload offset and length of the last value of an Any content (the value a map entry holds), or of the
    /// length-prefixed JSON text of a format or text of a string.
    /// </summary>
    public int ValueOffset { get; init; }
    public int ValueLength { get; init; }
    /// <summary>Payload offsets of the struct: its info byte, its content (items only) and the byte after it.</summary>
    public int ByteStart { get; init; }
    public int ContentStart { get; init; }
    public int ByteEnd { get; init; }
    public uint End => Clock + Length;
}

/// <summary>The structs one client contributes to a payload: clocks [Start, End) at struct indexes [First, First + Count).</summary>
internal readonly record struct YClientRange(ulong Client, uint Start, uint End, int First, int Count);

/// <summary>One delete-set entry: clocks [Clock, Clock + Length) of a client are deleted.</summary>
internal readonly record struct YDeleteRange(ulong Client, uint Clock, uint Length);

/// <summary>A fully decoded Yjs v1 payload (update or full state). Each client appears at most once, its structs contiguous.</summary>
internal sealed class YjsUpdate
{
    private readonly Dictionary<ulong, int> _clientIndex;

    public YjsUpdate(YStruct[] structs, YClientRange[] clients, YDeleteRange[] deleteSet)
    {
        Structs = structs;
        Clients = clients;
        DeleteSet = deleteSet;
        _clientIndex = new Dictionary<ulong, int>(clients.Length);
        for (var index = 0; index < clients.Length; index++)
            if (!_clientIndex.TryAdd(clients[index].Client, index))
                // Yjs keeps only the last list of a repeated client; refuse instead of guessing.
                throw new ContentRejectedException(ContentRejectionReason.Malformed, "client listed twice");
    }

    public YStruct[] Structs { get; }
    public YClientRange[] Clients { get; }
    public YDeleteRange[] DeleteSet { get; }

    public bool TryGetClient(ulong client, out YClientRange range)
    {
        if (_clientIndex.TryGetValue(client, out var index))
        {
            range = Clients[index];
            return true;
        }
        range = default;
        return false;
    }

    /// <summary>Index of the payload struct that contains the clock, or -1.</summary>
    public int FindStruct(YId id)
    {
        if (!TryGetClient(id.Client, out var range) || range.Count == 0 || id.Clock < range.Start || id.Clock >= range.End) return -1;
        var low = range.First;
        var high = range.First + range.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (Structs[middle].Clock <= id.Clock) low = middle;
            else high = middle - 1;
        }
        return low;
    }
}
