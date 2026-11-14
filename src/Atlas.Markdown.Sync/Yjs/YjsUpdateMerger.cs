namespace Atlas.Markdown.Sync;

/// <summary><see cref="Atlas.Yjs.YjsUpdateMerger"/>, where hosts already call it (the port moved to Atlas.Yjs).</summary>
public static class YjsUpdateMerger
{
    /// <inheritdoc cref="Atlas.Yjs.YjsUpdateMerger.Merge"/>
    public static byte[] Merge(IReadOnlyList<ReadOnlyMemory<byte>> payloads, long maxPayloadBytes) => Atlas.Yjs.YjsUpdateMerger.Merge(payloads, maxPayloadBytes);
}
