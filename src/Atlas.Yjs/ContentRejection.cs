namespace Atlas.Yjs;

/// <summary>Why a collaborative payload was refused. Stable names; clients see them after <c>[INVALID_CONTENT]</c>.</summary>
public enum ContentRejectionReason
{
    /// <summary>Not a well-formed lib0 v1 update (framing, varints, UTF-8, JSON, unknown content or type refs, trailing bytes).</summary>
    Malformed,

    /// <summary>A byte, struct, name, nesting or index bound was exceeded.</summary>
    LimitExceeded,

    /// <summary>A client's structs start after the known state (missing earlier updates) or the payload carries a skip.</summary>
    ClockGap,

    /// <summary>An origin, right origin or parent is neither known nor created earlier in the payload; Yjs would park it as pending.</summary>
    UnknownReference,

    /// <summary>A delete range reaches beyond the known state.</summary>
    DeleteOutOfRange,

    /// <summary>A snapshot contains clocks the document has never admitted.</summary>
    SnapshotExceedsState,

    /// <summary>Content under a root other than the editor fragment.</summary>
    OtherRoot,

    /// <summary>Embeds, binaries, JSON content, subdocuments or shared types the editor never creates.</summary>
    UnsupportedContent,

    /// <summary>An element name outside the node allowlist.</summary>
    UnknownNode,

    /// <summary>A format key outside the mark allowlist.</summary>
    UnknownMark,

    /// <summary>An attribute key the node does not declare.</summary>
    UnknownAttribute,

    /// <summary>Content in a position y-prosemirror never writes (text outside XmlText, attributes on roots or text, ...).</summary>
    InvalidStructure,

    /// <summary>A reserved node, or content inside one, that the host did not admit.</summary>
    ReservedNode,

    /// <summary>An attribute write on a reserved node that the host did not admit.</summary>
    ReservedAttribute,

    /// <summary>The document's stored state could not be indexed, so nothing can be admitted.</summary>
    IndexUnavailable,

    /// <summary>The guard failed unexpectedly and refused the payload to fail closed.</summary>
    GuardFault,

    /// <summary>An attribute value the editor would refuse (its value constraint, or not plain JSON data).</summary>
    InvalidAttributeValue,

    /// <summary>The document the payload produces breaks the editor schema: child types, marks, required attributes, plugin requirements, table bounds, depth (not the rules a merge of concurrent edits breaks).</summary>
    InvalidDocument,

    /// <summary>
    /// The guard's replica of the document would hold more items than its size allows (<see cref="YjsLimits.ItemBudget"/>):
    /// a payload of many tiny items no editor writes.
    /// </summary>
    TooManyItems,

    /// <summary>
    /// A snapshot that, with the updates after its sequence, shows another document than the admitted history: it leaves out
    /// admitted updates or deletes what nobody deleted.
    /// </summary>
    SnapshotMissesHistory,
}

/// <summary>The single failure the Yjs decoder, resolver, index and policy raise; admission turns it into a rejection.</summary>
internal sealed class ContentRejectedException(ContentRejectionReason reason, string detail) : Exception($"{reason}: {detail}")
{
    public ContentRejectionReason Reason { get; } = reason;
    public string Detail { get; } = detail;
}

/// <summary>Hard bounds of the Yjs content guard, each enforced before the corresponding allocation or loop.</summary>
internal static class YjsLimits
{
    /// <summary>Structs per payload. Real states carry one struct per 20–40 bytes.</summary>
    public const int MaxStructsPerPayload = 500_000;

    /// <summary>UTF-8 bytes of an element name, format key, attribute key or root name.</summary>
    public const int MaxNameBytes = 256;

    /// <summary>Nesting of lib0 Any values and of JSON format values (the JavaScript decoders recurse).</summary>
    public const int MaxValueDepth = 32;

    /// <summary>Indexed runs per document (about 32 B each), far above what a 4 MiB document produces.</summary>
    public const int MaxIndexSegments = 2_000_000;

    /// <summary>Indexed shared types per document.</summary>
    public const int MaxIndexTypes = 1_000_000;

    /// <summary>
    /// Items YATA conflict resolution may pass while one payload integrates. Real edits pass a few per struct; inserts of many
    /// clients at one position pass every earlier one (32,000 such structs, 360 KB, took 16 s).
    /// </summary>
    public const long MaxConflictScan = 10_000_000;

    /// <summary>
    /// Items the guard's replica of a document may hold after an update or snapshot: 65,536 plus one per 16 bytes the replica
    /// integrated (stored state, log and admitted payloads). The editor's states hold one item per 40 bytes and more (a
    /// 1 MiB document, 25,000 items); an encoded item takes at least about 10 bytes, so a hostile 4 MiB payload of 400,000
    /// one-character items exceeds it. A stored state is never refused for it.
    /// </summary>
    public static long ItemBudget(long integratedBytes) => 65_536 + integratedBytes / 16;
}
