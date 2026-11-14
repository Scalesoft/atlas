using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Managed validator of Yjs update v1 content, in process and without native code. Each payload is decoded in full
/// (content the same payload deletes again included), its references are resolved against a per-document index,
/// it is checked against <see cref="YjsContentPolicy"/> (names, attribute keys and every attribute value), applied to the
/// guard's replica of the document and the elements it changed are validated with the editor's document rules
/// (<see cref="MarkdownDocumentValidator"/>: child types, marks, required attributes, plugin requirements, table
/// bounds, depth; not the rules a merge of concurrent edits breaks), reserved node changes are decided by the host (<see cref="IReservedNodeGuard"/>, default reject), and
/// only then is the index updated; a rejected payload leaves the replica as it was. A snapshot or seed is validated whole.
/// </summary>
/// <remarks>
/// The index lives as long as the hub's in-memory baseline (<see cref="DocumentContentContext.Session"/>) and is built
/// on first use from the baseline and log the hub loaded from its <see cref="IDocumentStore"/>. Calls for one document
/// must be serialized, which the hub does with its per-document gate.
/// </remarks>
public sealed class ManagedDocumentContentGuard(
    YjsContentPolicy policy,
    MarkdownSyncOptions options,
    ILogger<ManagedDocumentContentGuard> log,
    IReservedNodeGuard? reservedNodes = null) : IDocumentContentGuard
{
    private readonly ConditionalWeakTable<object, IndexEntry> _indexes = new();

    public async ValueTask<ContentAdmission> AdmitUpdateAsync(DocumentContentContext context, ReadOnlyMemory<byte> update, CancellationToken ct)
    {
        try
        {
            return await AdmitUpdateAsync(IndexFor(context), context, update, ct).ConfigureAwait(false);
        }
        catch (ContentRejectedException rejection)
        {
            return ContentAdmission.Rejected(rejection.Reason, rejection.Detail);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Fault(context, error);
        }
    }

    /// <summary>
    /// A snapshot must be self-contained and pass the policy on its own (joining peers load it as is). For a seeded
    /// document it must not contain clocks the index has not admitted; an unseeded document's first snapshot (the
    /// seed) becomes its index.
    /// </summary>
    public async ValueTask<ContentAdmission> AdmitSnapshotAsync(DocumentContentContext context, ReadOnlyMemory<byte> state, CancellationToken ct)
    {
        try
        {
            var seed = context.Baseline is null && context.Log.Count == 0;
            var (admission, seeded) = await AdmitSnapshotAsync(seed ? null : IndexFor(context), context, state, ct).ConfigureAwait(false);
            if (seeded is not null) _indexes.AddOrUpdate(context.Session, new IndexEntry(seeded, null));
            return admission;
        }
        catch (ContentRejectedException rejection)
        {
            return ContentAdmission.Rejected(rejection.Reason, rejection.Detail);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Fault(context, error);
        }
    }

    /// <summary>Admission against an explicit index: scan, resolve, check, let the host decide reserved nodes, then record.</summary>
    internal async ValueTask<ContentAdmission> AdmitUpdateAsync(YjsDocumentIndex index, DocumentContentContext context, ReadOnlyMemory<byte> update,
        CancellationToken ct)
    {
        try
        {
            if (update.Length > options.MaxDocumentBytes)
                return ContentAdmission.Rejected(ContentRejectionReason.LimitExceeded, "update exceeds the document size limit");
            var parsed = YjsUpdateDecoder.Decode(update.Span, options.MaxDocumentBytes);
            var parents = YjsParentResolver.Resolve(parsed, index);
            var reserved = policy.Check(update.Span, parsed, parents, index);
            policy.CheckNoRetired(parsed);
            var tree = index.Tree;
            var settled = false;
            try
            {
                if (tree is not null)
                {
                    var changed = tree.Apply(update.Span, parsed, journal: true);
                    if (YjsTreeValidation.ValidateChanged(changed, policy.Validator!, Required(context)) is { } violation)
                        throw new ContentRejectedException(ContentRejectionReason.InvalidDocument, violation);
                }
                if (reserved.Count > 0) await AdmitReservedAsync(context, reserved, ReservedNodePayload.Update, index, ct).ConfigureAwait(false);
                index.Commit(update.Span, parsed, parents);
                tree?.Commit();
                settled = true;
            }
            finally
            {
                if (!settled) tree?.Rollback();
            }
            return ContentAdmission.Accepted;
        }
        catch (ContentRejectedException rejection)
        {
            return ContentAdmission.Rejected(rejection.Reason, rejection.Detail);
        }
    }

    /// <summary>Snapshot admission against the admitted index, or as the seed of an unseeded document when it is null.</summary>
    internal async ValueTask<(ContentAdmission Admission, YjsDocumentIndex? Seeded)> AdmitSnapshotAsync(YjsDocumentIndex? admitted,
        DocumentContentContext context, ReadOnlyMemory<byte> state, CancellationToken ct)
    {
        try
        {
            if (state.Length > options.MaxDocumentBytes)
                return (ContentAdmission.Rejected(ContentRejectionReason.LimitExceeded, "snapshot exceeds the document size limit"), null);
            var parsed = YjsUpdateDecoder.Decode(state.Span, options.MaxDocumentBytes);
            var standalone = new YjsDocumentIndex(policy.ReservedNodes, tree: policy.Validator is not null);
            if (admitted is not null)
                foreach (var range in parsed.Clients)
                    if (range.Count > 0 && range.End > admitted.GetClock(range.Client))
                        throw new ContentRejectedException(ContentRejectionReason.SnapshotExceedsState,
                            $"client {range.Client} reaches {range.End}, admitted {admitted.GetClock(range.Client)}");
            var parents = YjsParentResolver.Resolve(parsed, standalone);
            var reserved = policy.Check(state.Span, parsed, parents, standalone);
            if (standalone.Tree is { } tree)
            {
                tree.Apply(state.Span, parsed, journal: false);
                if (YjsTreeValidation.ValidateAll(tree, policy.Roots, policy.Validator!, Required(context)) is { } violation)
                    throw new ContentRejectedException(ContentRejectionReason.InvalidDocument, violation);
            }
            if (reserved.Count > 0)
                await AdmitReservedAsync(context, reserved, admitted is null ? ReservedNodePayload.Seed : ReservedNodePayload.Snapshot, admitted, ct)
                    .ConfigureAwait(false);
            if (admitted is not null) return (ContentAdmission.Accepted, null);
            standalone.Commit(state.Span, parsed, parents);
            return (ContentAdmission.Accepted, standalone);
        }
        catch (ContentRejectedException rejection)
        {
            return (ContentAdmission.Rejected(rejection.Reason, rejection.Detail), null);
        }
    }

    private YjsDocumentIndex IndexFor(DocumentContentContext context)
    {
        if (!_indexes.TryGetValue(context.Session, out var entry))
        {
            try
            {
                entry = new IndexEntry(YjsDocumentIndex.Build(context.Baseline,
                    context.Log.Select(record => (ReadOnlyMemory<byte>)record.Update), options.MaxDocumentBytes, policy.ReservedNodes,
                    tree: policy.Validator is not null), null);
            }
            catch (ContentRejectedException rejection)
            {
                // Fail closed, and remember it for this baseline so it is not re-read for every update.
                log.LogWarning("Yjs content guard cannot index {DocumentId}: {Reason} {Detail}", context.DocumentId, rejection.Reason, rejection.Detail);
                entry = new IndexEntry(null, $"{rejection.Reason}: {rejection.Detail}");
            }
            _indexes.AddOrUpdate(context.Session, entry);
        }
        return entry.Index ?? throw new ContentRejectedException(ContentRejectionReason.IndexUnavailable, entry.Failure!);
    }

    /// <summary>
    /// The plugins whose content the document may hold: those of its bound schema, or every plugin of the policy's schema for
    /// a document without one (legacy mode).
    /// </summary>
    private IReadOnlySet<string> Required(DocumentContentContext context)
        => (context.Schema?.Plugins.Select(plugin => plugin.Id) ?? policy.Validator!.Schema.Plugins.Keys).ToHashSet(StringComparer.Ordinal);

    /// <param name="admitted">What the server admitted before this payload (null for a seed): the source of each node's current attributes.</param>
    private async ValueTask AdmitReservedAsync(DocumentContentContext context, IReadOnlyList<YReservedWrite> writes, ReservedNodePayload payload,
        YjsDocumentIndex? admitted, CancellationToken ct)
    {
        var reason = writes.Any(write => write.IsNew) ? ContentRejectionReason.ReservedNode : ContentRejectionReason.ReservedAttribute;
        if (reservedNodes is null) throw new ContentRejectedException(reason, writes[0].NodeName);
        static Dictionary<string, JsonElement> Json(IEnumerable<KeyValuePair<string, byte[]>> values)
            => values.ToDictionary(pair => pair.Key, pair => Lib0AnyJson.ToJson(pair.Value), StringComparer.Ordinal);
        IReadOnlyDictionary<string, byte[]>? Current(YReservedWrite write)
        {
            if (admitted is null) return null;
            if (write.IndexType is { } existing) return admitted.GetAttributes(existing);
            // A snapshot sends every node again; one the server admitted before keeps its type id.
            return payload == ReservedNodePayload.Snapshot && write.PayloadType is { } sent && admitted.TryGetTypeId(sent, out var known)
                ? admitted.GetAttributes(known)
                : null;
        }
        var changes = writes
            .Select(write => new ReservedNodeChange(write.NodeName, write.IsNew, Json(write.Attributes))
            {
                CurrentAttributes = Current(write) is { Count: > 0 } current ? Json(current) : new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            })
            .ToArray();
        var request = new ReservedNodeRequest(context.DocumentId, context.AuthorUserId, context.PeerUserIds, changes) { Payload = payload };
        if (!await reservedNodes.AdmitAsync(request, ct).ConfigureAwait(false))
            throw new ContentRejectedException(reason, string.Join(", ", writes.Select(write => write.NodeName).Distinct(StringComparer.Ordinal)));
    }

    private ContentAdmission Fault(DocumentContentContext context, Exception error)
    {
        // A defect must not let content through, and the index may be half-updated: forget it.
        log.LogError(error, "Yjs content guard failed for {DocumentId}", context.DocumentId);
        _indexes.Remove(context.Session);
        return ContentAdmission.Rejected(ContentRejectionReason.GuardFault, "content guard failure");
    }

    private sealed class IndexEntry(YjsDocumentIndex? index, string? failure)
    {
        public YjsDocumentIndex? Index { get; } = index;
        public string? Failure { get; } = failure;
    }
}
