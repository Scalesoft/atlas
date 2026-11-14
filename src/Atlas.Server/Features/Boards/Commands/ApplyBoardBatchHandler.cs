using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.Boards.Queries;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Linking;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Retention;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards.Commands;

/// <summary>
/// One gesture (boards spec §5–6). Under the board row FOR UPDATE the batches of a board serialize, so the server's order decides
/// and the sequences have no gaps: a batch reads the committed state of the items it touches, BoardEditing applies it, and one
/// BoardOperation row records it with the next sequence. A retried operation id with the same changes answers the original
/// sequence; with other changes it is refused. Subscribers hear of the batch only after the commit, and the answer does not wait for them.
/// </summary>
public sealed class ApplyBoardBatchHandler
{
    private readonly BoardAccess m_boards;
    private readonly BoardPageTargets m_targets;
    private readonly BoardObjectTargets m_objects;
    private readonly BoardBatchWriter m_writer;

    public ApplyBoardBatchHandler(BoardAccess boards, BoardPageTargets targets, BoardObjectTargets objects, BoardBatchWriter writer)
    {
        m_boards = boards;
        m_targets = targets;
        m_objects = objects;
        m_writer = writer;
    }

    public async Task<BoardBatchResult> Handle(ApplyBoardBatch command, CancellationToken ct)
    {
        // The cheap refusals before any lock; BoardEditing checks the same again with the rest.
        if (command.OperationId is not { } operationId || operationId == Guid.Empty || command.Changes is not { Count: > 0 and <= BoardEditing.MaxChanges } requested)
        {
            throw BoardErrors.Rejected(BoardRejection.Invalid);
        }
        var changes = BoardEditing.Normalize(requested);
        var pageTargets = BoardEditing.PageTargets(changes);
        var opened = await m_boards.OpenAsync(command.BoardId, true, pageTargets, ct);
        var readable = (await m_targets.ReadableAsync(opened, pageTargets, ct)).Keys.ToHashSet();
        var objects = (await m_objects.ReadableAsync(opened.Actor, BoardEditing.ObjectTargets(changes), ct)).ToDictionary(target => target.Key, target => target.Value.Type);
        return await m_writer.WriteAsync(opened.Board, opened.Actor.Id, operationId, changes, readable, objects, ct);
    }
}

/// <summary>
/// Writes one normalized batch to a board whose row the caller holds FOR UPDATE, after deciding who writes it and which page and object
/// targets they may place (ApplyBoardBatchHandler for a person; a page's own board kept in step with its page by its module).
/// </summary>
public sealed class BoardBatchWriter
{
    private readonly ISession m_session;
    private readonly AccessChanges m_changes;
    private readonly LiveRelay m_relay;
    private readonly ViewSessions m_views;
    private readonly BoardBroadcasts m_broadcasts;
    private readonly IJobRequests m_jobs;
    private readonly long m_kept;

    public BoardBatchWriter(ISession session, AccessChanges changes, LiveRelay relay, ViewSessions views, BoardBroadcasts broadcasts, RetentionOptions retention,
        IJobRequests jobs)
    {
        m_session = session;
        m_jobs = jobs;
        m_changes = changes;
        m_relay = relay;
        m_views = views;
        m_broadcasts = broadcasts;
        // Only the last MaxBatches can be read back (GetBoardOperations), so more are never kept.
        m_kept = Math.Clamp(retention.Effective("BoardOperation").MaxRows ?? GetBoardOperationsHandler.MaxBatches, 1, GetBoardOperationsHandler.MaxBatches);
    }

    public async Task<BoardBatchResult> WriteAsync(Board board, Guid authorId, Guid operationId, IReadOnlyList<BoardChange> changes, IReadOnlySet<Guid> readablePages,
        IReadOnlyDictionary<Guid, string> objects, CancellationToken ct)
    {
        var json = BoardJson.Serialize(changes);
        var previous = await m_session.QueryOver<BoardOperation>()
            .Where(x => x.BoardId == board.Id && x.OperationId == operationId)
            .SingleOrDefaultAsync(ct);
        if (previous is not null)
        {
            // Both sides normalized and written by the same serializer, whatever wrote the stored row.
            var stored = BoardJson.Serialize(BoardEditing.Normalize(BoardJson.Deserialize(previous.ChangesJson)));
            return stored == json ? new BoardBatchResult(previous.Sequence, true) : throw BoardErrors.OperationConflict();
        }

        var touched = BoardEditing.TouchedIds(changes);
        var loaded = touched.Count == 0 ? [] : await m_session.QueryOver<BoardItem>().WhereRestrictionOn(x => x.Id).IsIn(touched.ToArray()).ListAsync(ct);
        var own = loaded.Where(item => item.BoardId == board.Id).ToDictionary(item => item.Id);
        var live = await m_session.QueryOver<BoardItem>().Where(x => x.BoardId == board.Id && !x.IsDeleted).RowCountAsync(ct);
        // Nesting is checked against every link of the board; only a batch that changes parents needs them.
        var parents = BoardEditing.ChangesParents(changes)
            ? (await m_session.QueryOver<BoardItem>().Where(x => x.BoardId == board.Id && x.ParentId != null)
                .Select(x => x.Id, x => x.ParentId).ListAsync<object[]>(ct)).ToDictionary(row => (Guid)row[0], row => (Guid)row[1])
            : null;
        var state = new BoardState(board.Title, own.Values.Select(item => item.ToState()).ToArray(), live,
            loaded.Where(item => item.BoardId != board.Id).Select(item => item.Id).ToHashSet(), readablePages, BoardProps.Canonical(board.SettingsJson), parents, objects);
        var sequence = board.Sequence + 1;
        var result = BoardEditing.Apply(state, changes, sequence);
        if (result.Rejection is { } rejection)
        {
            throw BoardErrors.Rejected(rejection);
        }

        // Created in the order of the batch; the connector ends are deferred keys, so the flush order does not matter.
        // Whether the search document and the references must follow: only what they read changed (not a colour, a move or a size).
        var indexed = result.Title != board.Title;
        foreach (var item in result.Changed)
        {
            if (own.TryGetValue(item.Id, out var existing))
            {
                indexed |= existing.Apply(item);
            }
            else
            {
                var created = BoardItem.Create(board.Id, item);
                indexed |= created.Text is not null || created.SearchText is not null;
                await m_session.SaveAsync(created, ct);
            }
        }
        // A colour or a size changes no byte the budget counts (Text and PropsJson).
        if (changes.Any(change => change?.Type is "create" or "restore" || change is { Type: "set" } and ({ Text: not null } or { Props: not null })))
        {
            await RequireBudgetAsync(board.Id, ct);
        }
        board.Title = result.Title;
        board.SettingsJson = result.Settings;
        await RecordAsync(board, authorId, operationId, changes, json, sequence, indexed, ct);
        return new BoardBatchResult(sequence, false);
    }

    /// <summary>
    /// Points every card of <paramref name="from"/> on the locked board at <paramref name="to"/> (a contact merge, contacts spec §5): the
    /// object of a card is fixed for a person's batch, so this is no batch of BoardEditing, but it is recorded as one, a <c>set</c> of the
    /// card's <c>objectId</c>, so an open board hears it live and a client catching up reads it back. The number of cards, deleted ones too.
    /// </summary>
    public async Task<int> RetargetAsync(Board board, Guid authorId, Guid from, Guid to, CancellationToken ct)
    {
        var cards = await m_session.QueryOver<BoardItem>().Where(x => x.BoardId == board.Id && x.TargetResourceId == from).ListAsync(ct);
        if (cards.Count == 0)
        {
            return 0;
        }
        var sequence = board.Sequence + 1;
        var changes = new List<BoardChange>();
        foreach (var card in cards)
        {
            var props = JsonNode.Parse(card.PropsJson)!.AsObject();
            if (props["objectId"] is not null)
            {
                props["objectId"] = to.ToString();
            }
            card.TargetResourceId = to;
            card.PropsJson = BoardJson.Write(BoardProps.Canonical(props));
            card.Version = sequence;
            if (!card.IsDeleted)
            {
                changes.Add(new BoardChange("set", ItemId: card.Id, Props: new JsonObject { ["objectId"] = to.ToString() }));
            }
        }
        if (changes.Count > 0)
        {
            await RecordAsync(board, authorId, Guid.NewGuid(), changes, BoardJson.Serialize(changes), sequence, true, ct);
        }
        return cards.Count;
    }

    /// <summary>The live items as they leave this batch fit BoardEditing.MaxBoardBytes (L3), counted uncompressed, as a read loads them;
    /// else the whole batch is refused (the transaction rolls back what the session wrote).</summary>
    private async Task RequireBudgetAsync(Guid boardId, CancellationToken ct)
    {
        await m_session.FlushAsync(ct);
        var bytes = await m_session.CreateSQLQuery("""
                SELECT coalesce(sum(coalesce(octet_length("Text"),0) + octet_length(CAST("PropsJson" AS text))),0) AS bytes
                FROM "BoardItem" WHERE "BoardId"=:board AND NOT "IsDeleted"
                """)
            .AddScalar("bytes", NHibernate.NHibernateUtil.Int64).SetParameter("board", boardId).UniqueResultAsync<long>(ct);
        if (bytes > BoardEditing.MaxBoardBytes)
        {
            throw BoardErrors.TooLarge();
        }
    }

    /// <summary>The batch's log row and its live message after the commit.</summary>
    private async Task RecordAsync(Board board, Guid authorId, Guid operationId, IReadOnlyList<BoardChange> changes, string json, long sequence, bool indexed, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        board.Sequence = sequence;
        board.UpdatedAt = now;
        await m_session.SaveAsync(new BoardOperation
        {
            BoardId = board.Id, Sequence = sequence, OperationId = operationId, AuthorId = authorId, AppliedAt = now, ChangesJson = json
        }, ct);
        // The sync log's retention by count (the age is OperationalRetention's): the batches no client can read back any more.
        await m_session.CreateSQLQuery("""DELETE FROM "BoardOperation" WHERE "BoardId"=:board AND "Sequence"<=:oldest""")
            .SetParameter("board", board.Id).SetParameter("oldest", sequence - m_kept).ExecuteUpdateAsync(ct);
        if (indexed)
        {
            await BoardSearch.RefreshAsync(m_session, board.Id, ct);
            // The items' mentions (ContentReferenceStore.RefreshBoardAsync); a burst of batches merges into one pass.
            await ContentReferenceJob.RequestAsync(m_jobs, "Úprava nástěnky", ct);
        }
        var message = new BoardChangedMessage(board.Id, sequence, operationId, authorId, changes);
        // Who may not write hears the batch as GET shows the board after it (BoardEditing.ForReader); null when that is all of it.
        var hidden = await GetBoardOperationsHandler.HiddenAsync(m_session, board.Id, changes, ct);
        var forReaders = hidden.Count == 0 ? null : message with { Changes = BoardEditing.ForReader(changes, hidden, new HashSet<Guid>()) };
        // After the commit and outside the request (BoardBroadcasts): the board's subscribers, then the board as an object, whose view
        // sessions hear the batch and read their membership again.
        m_changes.AfterCommit(() =>
        {
            m_broadcasts.Enqueue(board.Id, async () =>
            {
                await m_relay.PublishAsync(BoardLiveChannel.Name(board.Id), "BoardChanged", message, readerPayload: forReaders);
                await m_views.ObjectChangedAsync(new ObjectChangedMessage(board.Id, sequence, JsonSerializer.SerializeToNode(message, JsonSerializerOptions.Web)),
                    readerChange: forReaders is null ? null : JsonSerializer.SerializeToNode(forReaders, JsonSerializerOptions.Web));
            });
            return Task.CompletedTask;
        });
    }
}
