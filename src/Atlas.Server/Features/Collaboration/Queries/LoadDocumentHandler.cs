using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Queries;

public sealed class LoadDocumentHandler
{
    private readonly ISession m_session;

    public LoadDocumentHandler(ISession session)
    {
        m_session = session;
    }

    public async Task<StoredDocument> Handle(LoadDocument query, CancellationToken ct)
    {
        var state = await LiveDocument.LockAsync(m_session, query.PageId, query.Generation, ct);
        // The base revision only seeds a generation without a stored snapshot.
        var revision = state.StateSha256 is null
            ? await m_session.GetAsync<PageRevision>(state.BaseRevisionId, ct) ?? throw CollaborationErrors.Stale()
            : null;
        // Content was validated on the way in (Rules.ValidateDocument, the collaboration guard and FlushDocument); only accounts
        // that may Discover every reference join (AuthorizeCollaborator), so the relay serves the state unfiltered.
        using var rich = revision is null ? null : JsonDocument.Parse(revision.ContentJson, MarkdownDocumentValidator.JsonOptions);
        var schema = CollaborationSchema.Deserialize(state.SchemaJson);
        var logs = await ReadLogAsync(query.PageId, query.Generation, ct);
        return new StoredDocument(revision is null ? state.State!.ToArray() : null,
            logs.Where(update => update.Seq > state.SnapshotSequence).ToArray(), state.Sequence,
            revision?.Markdown, false,
            logs.ToDictionary(update => update.ClientUpdateId, update => update.Seq))
        {
            Schema = schema,
            SeedEditorJson = rich?.RootElement.GetProperty("document").Clone(),
            RecentUpdates = logs.ToDictionary(update => update.ClientUpdateId,
                update => new StoredRecentUpdate(update.Seq, update.Update.ToArray()))
        };
    }

    private async Task<IReadOnlyList<LogUpdate>> ReadLogAsync(Guid pageId, Guid generation, CancellationToken ct)
    {
        var rows = await m_session.QueryOver<CollaborationUpdate>()
            .Where(x => x.PageId == pageId && x.Generation == generation)
            .OrderBy(x => x.Sequence).Asc
            .ListAsync(ct);
        return rows
            .Select(row => new LogUpdate(row.Sequence, row.ClientUpdateId, row.UserId.ToString(), new DateTimeOffset(row.ReceivedAt), row.Payload))
            .ToArray();
    }
}
