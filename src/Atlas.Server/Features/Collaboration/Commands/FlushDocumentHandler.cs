using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Commands;

public sealed class FlushDocumentHandler
{
    /// <summary>Every plugin the web installs, at its version: the snapshot carries only the doc node, so it is validated as a
    /// document of the current editor that may use any of them.</summary>
    internal static readonly string Plugins = JsonSerializer.Serialize(HostDocumentValidator.Instance.Schema.Plugins.Values.Select(x => new { id = x.Id, version = x.Version }));

    private readonly ISession m_session;
    private readonly CollaborationLog m_log;
    private readonly CollaborationSecretReferences m_references;
    private readonly ILogger<FlushDocumentHandler> m_logger;

    public FlushDocumentHandler(ISession session, CollaborationLog log, CollaborationSecretReferences references, ILogger<FlushDocumentHandler> logger)
    {
        m_session = session;
        m_log = log;
        m_references = references;
        m_logger = logger;
    }

    public async Task<FlushResult> Handle(FlushDocument command, CancellationToken ct)
    {
        var snapshot = command.Snapshot;
        // Nothing a client wrote is stored unvalidated: the projection must be a document every other write would accept
        // (Rules.ValidateDocument); no Markdown or plain text of it is stored.
        IReadOnlySet<Guid> referenced = new HashSet<Guid>();
        ApiException? refused = null;
        try
        {
            (referenced, _) = Rules.ValidateDocument($"{{\"format\":\"{MarkdownDocumentValidator.Format}\",\"version\":1,\"plugins\":{Plugins},\"document\":{snapshot.Projections.EditorJson.GetRawText()}}}");
        }
        catch (ApiException error)
        {
            refused = error;
        }
        PageCollaboration state;
        try
        {
            state = await LiveDocument.LockAsync(m_session, command.PageId, command.Generation, ct);
        }
        catch (ApiException error) when (error.Status == 409)
        {
            // A save, restore, discard or trash moved the page to a new generation after this snapshot was taken (their reset
            // already decided what its edits become). Retrying could never store it and kept the relay failing every tick.
            m_logger.LogInformation("Live snapshot {Sequence} of page {PageId} belongs to a replaced generation {Generation}; dropped",
                snapshot.CoveredSequence, command.PageId, command.Generation);
            return FlushResult.Superseded;
        }
        if (snapshot.CoveredSequence > state.Sequence)
        {
            throw new InvalidOperationException("Snapshot sequence is outside the persisted document range.");
        }
        if (snapshot.CoveredSequence < state.SnapshotSequence)
        {
            // A newer one is stored already (the server's checkpoint of a live AI write, 3.6-H, while the relay flushed an older one).
            return FlushResult.Superseded;
        }
        if (refused is not null)
        {
            // D-M3: the snapshot stays unstored and the log unpruned, but the refusal is recorded for the save of the live document to
            // report (CollaborationCheckpoints), and the relay is answered as flushed so it does not retry the same snapshot every tick;
            // the editor's next snapshot is validated again and replaces it.
            state.RefusedSequence = snapshot.CoveredSequence;
            state.Refusal = FlushRefusal.Of(refused);
            state.UpdatedAt = DateTime.UtcNow;
            m_logger.LogWarning("Live snapshot {Sequence} of page {PageId} refused as page content: {Code} at {Path}", snapshot.CoveredSequence,
                command.PageId, refused.Code, refused.Extensions?.GetValueOrDefault("path"));
            return FlushResult.None;
        }
        // The relay derives the projection from the admitted state (MarkdownSyncHub.PushSnapshot); still, it may name only references
        // the collaboration guard admitted into this generation (or its base revision holds), so nothing can smuggle in a reference nobody decided.
        if (referenced.Count > 0 && !referenced.IsSubsetOf(await m_references.OfAsync(command.PageId, command.Generation, state.BaseRevisionId, ct)))
        {
            throw new InvalidDataException("The snapshot projection names a password reference the document never admitted.");
        }
        state.SnapshotSequence = snapshot.CoveredSequence;
        state.State = snapshot.YjsState;
        state.StateSha256 = snapshot.Projections.StateSha256;
        state.ProjectionJson = snapshot.Projections.EditorJson.GetRawText();
        state.RefusedSequence = null;
        state.Refusal = null;
        state.SchemaBound = true;
        state.UpdatedAt = DateTime.UtcNow;
        var sizes = await m_log.SizesAsync(command.PageId, command.Generation, ct);
        state.PendingCount = sizes.Count(entry => entry.Sequence > state.SnapshotSequence);
        state.PendingBytes = sizes.Where(entry => entry.Sequence > state.SnapshotSequence).Sum(entry => (long)entry.Bytes);
        await m_log.PruneAsync(command.PageId, command.Generation, state.SnapshotSequence, sizes, ct);
        return FlushResult.None;
    }
}
