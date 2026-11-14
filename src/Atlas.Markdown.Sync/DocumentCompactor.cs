using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Atlas.Markdown.Sync;

/// <summary>
/// The relay's own snapshot when the elected client cannot keep up (a large page under many writers): once the update log above
/// the snapshot holds <see cref="MarkdownSyncOptions.ServerCompactionUpdates"/> records, the snapshot and the log are merged
/// (<see cref="YjsUpdateMerger"/>, the bytes Y.mergeUpdates writes) and the editor JSON is materialized from the result
/// (<see cref="YjsDocumentMaterializer"/>), off the document gate. The result is adopted like a pushed snapshot only when the
/// merged state shows exactly the document the snapshot and the log show, and the document still has the history it was taken
/// from; the flush service then stores it. Without the managed content guard (no schema to materialize with) it does nothing.
/// The Markdown and plain text projections of such a snapshot are empty: hosts derive their own from the editor JSON.
/// </summary>
/// <remarks>
/// No garbage collection: the merge keeps deleted content (Y.mergeUpdates semantics, as the managed authority's checkpoint, the page
/// bundle's live state and the live write do); only a browser's snapshot (a <c>gc: true</c> document) drops it. What bounds a document
/// no browser snapshots (Atlas): every update, a host's live write included, is refused once the snapshot and the log would pass
/// <see cref="MarkdownSyncOptions.MaxDocumentBytes"/>. Atlas's live write (LivePageWrites) runs only while someone is in the
/// session or has unsaved edits, else the agent's revision starts a new generation; and a live write that cannot be appended
/// (DOCUMENT_FULL included) resets the generation to that revision, after people's edits were saved under the same gate. So a page
/// an agent keeps rewriting while only a reader (or no browser that snapshots) is present grows up to the limit and then starts
/// over from its revision, nothing lost (McpLiveWriteTests.Agent_rewrites_without_a_browser_snapshot_keep_the_live_state_within_the_document_limit).
/// A writer's browser snapshots after every batch of changes (the snapshotter), which collects the garbage long before.
/// </remarks>
public sealed class DocumentCompactor(MarkdownSyncOptions options, DocumentRegistry registry, IServiceProvider services, ILogger<DocumentCompactor> log)
{
    private readonly Lazy<YjsDocumentMaterializer?> _materializer = new(() =>
        services.GetService<YjsContentPolicy>()?.Validator?.Schema is { } schema
            ? new YjsDocumentMaterializer(schema, maxPayloadBytes: options.MaxDocumentBytes)
            : null);

    /// <summary>The materializer of the managed content guard's schema; null for an opaque relay.</summary>
    internal YjsDocumentMaterializer? Materializer => _materializer.Value;

    /// <summary>Start a compaction when the log is due. Call under the document gate, after a durable append.</summary>
    internal void Consider(LiveDocument doc)
    {
        var threshold = options.ServerCompactionUpdates;
        if (threshold <= 0 || doc.CompactionRunning || doc.Log.Count < threshold || doc.Seq < doc.CompactionRetryAfter
            || doc.Snapshot is null || !doc.Seeded || doc.SeedInProgress || doc.AuthorityEpoch is not null || _materializer.Value is null)
            return;
        doc.CompactionRunning = true;
        var work = new Work(doc, doc.ContentSession, doc.Snapshot, doc.Log.Select(record => record.Update).ToArray(), doc.Seq);
        _ = Task.Run(() => RunAsync(work));
    }

    internal sealed record Work(LiveDocument Doc, object Session, byte[] Baseline, byte[][] Updates, long Sequence);

    internal async Task RunAsync(Work work)
    {
        var doc = work.Doc;
        DocSnapshot? snapshot = null;
        try
        {
            snapshot = Compact(work);
        }
        catch (Exception error)
        {
            log.LogWarning(error, "Collab doc {DocumentId} could not be compacted on the server at seq {Sequence}", doc.DocumentId, work.Sequence);
        }
        await doc.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            doc.CompactionRunning = false;
            if (snapshot is null)
            {
                doc.CompactionRetryAfter = work.Sequence + options.ServerCompactionUpdates;
                return;
            }
            // A reload, reseed, V2 cutover or a newer snapshot since the capture: this one no longer describes the document.
            if (!ReferenceEquals(registry.Current(doc.DocumentId), doc) || !ReferenceEquals(doc.ContentSession, work.Session)
                || doc.AuthorityEpoch is not null || !doc.Seeded || doc.SnapshotSeq >= work.Sequence || doc.Seq < work.Sequence)
                return;
            doc.AdoptSnapshot(snapshot.YjsState, snapshot.Projections, work.Sequence);
            log.LogInformation("Collab doc {DocumentId} compacted on the server at seq {Sequence} ({Updates} updates, {Bytes} bytes)",
                doc.DocumentId, work.Sequence, work.Updates.Length, snapshot.YjsState.Length);
            // Updates admitted while this one ran may already make the next one due.
            Consider(doc);
        }
        finally
        {
            doc.Gate.Release();
        }
    }

    private DocSnapshot? Compact(Work work)
    {
        var materializer = _materializer.Value!;
        var merged = YjsUpdateMerger.Merge([work.Baseline, .. work.Updates.Select(update => (ReadOnlyMemory<byte>)update)], options.MaxDocumentBytes);
        if (merged.Length > options.MaxDocumentBytes) return null;
        var json = materializer.Materialize(merged);
        // Never adopt a state that shows anything but what the admitted history shows.
        if (json != materializer.Materialize(work.Baseline, work.Updates.Select(update => (ReadOnlyMemory<byte>)update)))
        {
            log.LogError("Collab doc {DocumentId}: the merged state at seq {Sequence} differs from its history; not adopted", work.Doc.DocumentId, work.Sequence);
            return null;
        }
        using var editorJson = JsonDocument.Parse(json);
        var hash = Convert.ToHexString(SHA256.HashData(merged)).ToLowerInvariant();
        return SnapshotValidation.Prepare(new DocSnapshot(merged, work.Sequence,
            new ProjectionSetDto(work.Sequence, hash, "", editorJson.RootElement.Clone(), "")));
    }
}
