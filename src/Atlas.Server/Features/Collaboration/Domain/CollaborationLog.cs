using Atlas.Markdown.Sync;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>The durable update log: bounded idempotency receipts below the snapshot, an untouched recovery tail above it.</summary>
public sealed class CollaborationLog
{
    // The relay's idempotency window (Atlas.Markdown.Sync 0.4): retain at least what the relay still recognizes.
    public const int RecentCapacity = RecentUpdateWindow.MaxEntries;
    public const long RecentPayloadBytes = RecentUpdateWindow.MaxPayloadBytes;
    private readonly ISession m_session;

    public CollaborationLog(ISession session)
    {
        m_session = session;
    }

    /// <summary>Sequences and payload sizes of one generation, newest first. Never loads the payload bytes.</summary>
    public async Task<IReadOnlyList<(long Sequence, int Bytes)>> SizesAsync(Guid pageId, Guid generation, CancellationToken ct)
    {
        // The projection has its own query space, so pending inserts are invisible until they reach the database.
        await m_session.FlushAsync(ct);
        var rows = await m_session.QueryOver<CollaborationUpdateSize>()
            .Where(x => x.PageId == pageId && x.Generation == generation)
            .OrderBy(x => x.Sequence).Desc
            .SelectList(list => list.Select(x => x.Sequence).Select(x => x.Bytes))
            .ListAsync<object[]>(ct);
        return rows.Select(row => ((long)row[0], (int)row[1])).ToArray();
    }

    public async Task PruneAsync(Guid pageId, Guid generation, long snapshotSequence, IReadOnlyList<(long Sequence, int Bytes)> sizes, CancellationToken ct)
    {
        long? bound = null;
        long bytes = 0;
        for (var ordinal = 1; ordinal <= sizes.Count; ordinal++)
        {
            var entry = sizes[ordinal - 1];
            bytes += entry.Bytes;
            if (ordinal > RecentCapacity || bytes > RecentPayloadBytes)
            {
                // Newest first, so both bounds are downward closed: this sequence and everything below it goes.
                bound = entry.Sequence;
                break;
            }
        }
        if (bound is not { } limit)
        {
            return;
        }
        // The pending insert must reach the database before the bulk delete decides what is still there.
        await m_session.FlushAsync(ct);
        await m_session.CreateQuery("delete from CollaborationUpdate where PageId = :page and Generation = :generation and Sequence <= :bound")
            .SetParameter("page", pageId)
            .SetParameter("generation", generation)
            .SetParameter("bound", Math.Min(limit, snapshotSequence))
            .ExecuteUpdateAsync(ct);
    }
}
