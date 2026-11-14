using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Queries;

public sealed class GetBoardOperationsHandler
{
    /// <summary>More batches than this since the client's sequence: it reloads the board instead (resync_required).</summary>
    public const int MaxBatches = 500;

    /// <summary>
    /// At most this many bytes of stored changes (UTF-8, BoardJson) in one answer, else resync_required as for MaxBatches, also
    /// when the first missed batch alone is larger (200 changes of 4000 characters may be). 2 MB, as the largest page document a
    /// client loads (Rules.MaxDocumentJsonLength): catching up never costs more than opening a page. The HTTP answer escapes
    /// non-ASCII text, so on the wire it may be up to three times this.
    /// </summary>
    public const int MaxBytes = 2_000_000;

    private readonly ISession m_session;
    private readonly BoardAccess m_boards;

    public GetBoardOperationsHandler(ISession session, BoardAccess boards)
    {
        m_session = session;
        m_boards = boards;
    }

    /// <summary>The committed batches after <c>After</c>, in order, for a client that missed some (a gap, a reconnect).</summary>
    public async Task<BoardOperationsDto> Handle(GetBoardOperations query, CancellationToken ct)
    {
        if (query.After < 0)
        {
            throw new ApiException(400, "Parametr after musí být nezáporné číslo.");
        }
        var opened = await m_boards.OpenAsync(query.BoardId, false, [], ct);
        var board = opened.Board;
        // The log has no gaps (every batch adds one under the board's row lock, which this read holds FOR SHARE), so the number
        // of missed batches is known before any is read.
        var missed = board.Sequence - query.After;
        if (missed > MaxBatches)
        {
            throw BoardErrors.ResyncRequired();
        }
        // Summed in SQL from the stored sizes (octet_length takes a TOASTed value's size without loading it), so no batch past
        // the budget is read; what is read is a prefix of the missed ones.
        var rows = await m_session.CreateSQLQuery("""
                SELECT {o.*} FROM (SELECT *, sum(octet_length("ChangesJson")) OVER (ORDER BY "Sequence") AS "Total"
                    FROM "BoardOperation" WHERE "BoardId"=:board AND "Sequence">:after) {o}
                WHERE "Total"<=:budget ORDER BY {o}."Sequence"
                """)
            .AddEntity("o", typeof(BoardOperation))
            .SetParameter("board", board.Id)
            .SetParameter("after", query.After)
            .SetParameter("budget", (long)MaxBytes)
            .ListAsync<BoardOperation>(ct);
        if (rows.Count < missed)
        {
            throw BoardErrors.ResyncRequired();
        }
        var batches = rows.Select(row => new BoardBatchDto(row.Sequence, row.OperationId, row.AuthorId, BoardJson.Deserialize(row.ChangesJson))).ToArray();
        if (!opened.CanEdit && await HiddenAsync(m_session, board.Id, batches.SelectMany(batch => batch.Changes), ct) is { Count: > 0 } hidden)
        {
            // Every batch stays, its sequence too, so the client sees no gap; in order, so a create left out hides the item's later changes.
            var untold = new HashSet<Guid>();
            batches = batches.Select(batch => batch with { Changes = BoardEditing.ForReader(batch.Changes, hidden, untold) }).ToArray();
        }
        // A reader catches up to the board as it is now, not to the values a later batch replaced (L1).
        return new BoardOperationsDto(board.Sequence, opened.CanEdit ? batches : BoardEditing.LatestValues(batches));
    }

    /// <summary>The items <paramref name="changes"/> create or set that a reader of the board does not see now, by the rule of GET
    /// (BoardEditing.Visible): deleted, gone, or a connector with a deleted end. The session is flushed first, so a batch being written
    /// is decided as it leaves the board.</summary>
    public static async Task<IReadOnlySet<Guid>> HiddenAsync(ISession session, Guid boardId, IEnumerable<BoardChange> changes, CancellationToken ct)
    {
        var ids = changes.Where(change => change.Type is "create" or "set").Select(change => change.Item?.Id ?? change.ItemId).OfType<Guid>().ToHashSet();
        if (ids.Count == 0)
        {
            return ids;
        }
        await session.FlushAsync(ct);
        var items = (await session.QueryOver<BoardItem>().Where(x => x.BoardId == boardId).And(GuidList.In("Id", ids)).ListAsync(ct)).Select(item => item.ToState()).ToList();
        var ends = items.SelectMany(item => new[] { item.FromItemId, item.ToItemId }).OfType<Guid>().Where(id => !ids.Contains(id)).ToHashSet();
        if (ends.Count > 0)
        {
            items.AddRange((await session.QueryOver<BoardItem>().Where(x => x.BoardId == boardId).And(GuidList.In("Id", ends)).ListAsync(ct)).Select(item => item.ToState()));
        }
        ids.ExceptWith(BoardEditing.Visible(items).Select(item => item.Id));
        return ids;
    }
}
