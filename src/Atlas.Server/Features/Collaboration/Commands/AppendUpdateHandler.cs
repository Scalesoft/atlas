using NHibernate;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Commands;

public sealed class AppendUpdateHandler
{
    private readonly ISession m_session;
    private readonly CollaborationAccess m_access;
    private readonly Outbox m_outbox;
    private readonly MarkdownSyncOptions m_options;
    private readonly JobQuiet m_quiet;

    public AppendUpdateHandler(ISession session, CollaborationAccess access, Outbox outbox, MarkdownSyncOptions options, JobQuiet quiet)
    {
        m_quiet = quiet;
        m_session = session;
        m_access = access;
        m_outbox = outbox;
        m_options = options;
    }

    public async Task<AppendUpdateResult> Handle(AppendUpdate command, CancellationToken ct)
    {
        m_quiet.Edited();
        // The hub already checks the connection's stamp; the store rechecks current rights at the write boundary.
        await m_access.RequireWriterAsync(command.PageId, command.AuthorId, null, ct);
        var head = await m_access.LockHeadAsync(command.PageId, command.Generation, ct);
        // A person's update (no origin: the hub) never lands on a generation that lacks the current revision: the live write appending
        // it holds the document gate, so this is one that never will (the process ended between its commit and its append). Its
        // writers rejoin, and OpenCollaboration starts the page again from the revision, which holds everything saved before.
        if (head.Behind && command.Origin is null)
        {
            throw CollaborationErrors.Stale();
        }
        var previous = await m_session.QueryOver<CollaborationUpdate>()
            .Where(x => x.PageId == command.PageId && x.Generation == command.Generation && x.ClientUpdateId == command.ClientUpdateId)
            .SingleOrDefaultAsync(ct);
        if (previous is not null)
        {
            if (!command.Update.AsSpan().SequenceEqual(previous.Payload))
            {
                throw new InvalidDataException("An update identifier cannot be reused with a different payload.");
            }
            return new AppendUpdateResult(previous.Sequence, true);
        }
        // Running totals (COL2-6): re-reading the log's sizes here made every update cost the whole pending window.
        if (head.PendingCount >= CollaborationLog.RecentCapacity || head.PendingBytes + command.Update.Length > CollaborationLog.RecentPayloadBytes)
        {
            // Back-pressure, not a loss: the relay answers with an error the client retries until a snapshot shortens the log.
            throw new InvalidOperationException("A snapshot is required before accepting more pending updates.");
        }
        var nextSequence = checked(head.Sequence + 1);
        // Bulk update: loading the entity here would drag the multi-megabyte snapshot and projections into the session.
        await m_session.CreateQuery("update PageCollaboration set Sequence = :sequence, PendingCount = PendingCount + 1, PendingBytes = PendingBytes + :bytes, "
                + "SchemaBound = true, UpdatedAt = :now where PageId = :page and Generation = :generation")
            .SetParameter("sequence", nextSequence)
            .SetParameter("bytes", (long)command.Update.Length)
            .SetParameter("now", DateTime.UtcNow, NHibernateUtil.UtcDateTime)
            .SetParameter("page", command.PageId)
            .SetParameter("generation", command.Generation)
            .ExecuteUpdateAsync(ct);
        await m_session.SaveAsync(new CollaborationUpdate
        {
            PageId = command.PageId,
            Generation = command.Generation,
            Sequence = nextSequence,
            ClientUpdateId = command.ClientUpdateId,
            UserId = command.AuthorId,
            ReceivedAt = command.ReceivedAtUtc.UtcDateTime,
            Payload = command.Update,
            Channel = command.Origin?.Channel,
            ApiTokenId = command.Origin?.ApiTokenId,
            AiConversationId = command.Origin?.ConversationId
        }, ct);
        // Receipts below the snapshot are pruned when the next snapshot is stored (FlushDocumentHandler), never here.
        if (head.Sequence <= head.LastSavedSequence)
        {
            // The first unsaved update: the server stores the page at the latest this long after it (LiveCheckpoints).
            m_outbox.Enqueue(new CheckpointLivePage(command.PageId, command.Generation), command.PageId.ToString(), m_options.ServerCheckpointInterval);
        }
        return new AppendUpdateResult(nextSequence, false);
    }
}
