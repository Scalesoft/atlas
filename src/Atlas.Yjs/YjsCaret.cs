namespace Atlas.Yjs;

/// <summary>
/// Where a written update leaves its writer's caret, as the id a Yjs relative position names with assoc −1 (right after that unit):
/// the last text the update inserts, or else the last unit it deletes (a deletion leaves the caret where the text was).
/// </summary>
public static class YjsCaret
{
    public static (ulong Client, uint Clock)? AfterWrite(ReadOnlySpan<byte> update, long maxPayloadBytes)
    {
        var decoded = YjsUpdateDecoder.Decode(update, maxPayloadBytes);
        for (var index = decoded.Structs.Length - 1; index >= 0; index--)
        {
            if (decoded.Structs[index] is { Content: YContentKind.String } text) return (text.Client, text.End - 1);
        }
        return decoded.DeleteSet is [.., var deleted] ? (deleted.Client, deleted.Clock + deleted.Length - 1) : null;
    }
}
