using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.Boards.Queries;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Queries;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Platform.Live;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Atlas.Server.Tests.Boards.BoardsFixture;

namespace Atlas.Server.Tests.Boards;

/// <summary>The board handlers on PostgreSQL (boards spec §6): roles, page cards, idempotency, serialization and the
/// post-commit broadcast.</summary>
public sealed class BoardHandlerTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task A_writer_edits_the_board_a_reader_reads_and_nobody_else_learns_it_exists()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), link = Guid.NewGuid();
        Assert.Equal(new BoardBatchResult(1, false), await f.ApplyAsync(f.Writer, boardId, Note(a, "first"), Note(b, x: 300),
            new BoardChange("create", Item: new BoardItemInput(link, "connector", 0, 0, 8, 8, 1, From: a, To: b, Props: Elbow())),
            new BoardChange("rename", Title: "Roadmap")));

        var read = await f.As<BoardDto>(f.Reader, new GetBoard(boardId));
        Assert.Equal((boardId, f.SpaceId, "Roadmap", 1L, false), (read.Id, read.SpaceId, read.Title, read.Sequence, read.CanEdit));
        Assert.Equal(new[] { a, b, link }.Order(), read.Items.Select(item => item.Id).Order());
        var first = read.Items.Single(item => item.Id == a);
        Assert.Equal(("first", "yellow", 1L), (first.Text, first.Color, first.Version));
        Assert.Equal((a, b), read.Items.Single(item => item.Id == link) is { From: { } from, To: { } to } ? (from, to) : default);
        Assert.True((await f.As<BoardDto>(f.Writer, new GetBoard(boardId))).CanEdit);
        var listed = Assert.Single((await f.As<BoardList>(f.Reader, new ListBoards(f.SpaceId))).Boards);
        Assert.Equal((boardId, "Roadmap", 3), (listed.Id, listed.Title, listed.ItemCount));

        // A deleted end hides its connector; the list counts what a reader sees, so the hidden connector is not counted.
        await f.ApplyAsync(f.Writer, boardId, new BoardChange("delete", ItemId: b));
        Assert.Equal([a], (await f.As<BoardDto>(f.Reader, new GetBoard(boardId))).Items.Select(item => item.Id));
        Assert.Equal(1, Assert.Single((await f.As<BoardList>(f.Reader, new ListBoards(f.SpaceId))).Boards).ItemCount);

        // The reader may know the board, so a write is 403; an account without a role gets 404 for all of it, as for pages.
        await Refused(403, () => f.ApplyAsync(f.Reader, boardId, Note(Guid.NewGuid())));
        await Refused(403, () => f.As(f.Reader, new DeleteBoard(boardId)));
        await Refused(403, () => f.As<BoardDto>(f.Reader, new CreateBoard(f.SpaceId, "Mine")));
        await Refused(404, () => f.As<BoardDto>(f.Outsider, new GetBoard(boardId)));
        await Refused(404, () => f.ApplyAsync(f.Outsider, boardId, Note(Guid.NewGuid())));
        await Refused(404, () => f.As<BoardOperationsDto>(f.Outsider, new GetBoardOperations(boardId, 0)));
        await Refused(404, () => f.As<BoardList>(f.Outsider, new ListBoards(f.SpaceId)));
        await Refused(404, () => f.As<BoardDto>(f.Writer, new GetBoard(Guid.NewGuid())));
        Assert.Equal(2L, (await f.As<BoardDto>(f.Writer, new GetBoard(boardId))).Sequence);
    }

    /// <summary>The page's board (Board.PagePurpose, spec 3.5.1 §1.10) is decided by its page alone: who may edit the page makes and edits
    /// it, who may read the page reads it (not in the tree, only as the page's mark), nobody else learns it exists; an unpublished page's
    /// board is its owner's, as the page; a page's board of another purpose stays its editors' (review S4-3).</summary>
    [PostgreSqlFact]
    public async Task The_page_board_is_read_by_who_reads_the_page_and_edited_by_who_edits_it()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Nasazení", IndexFixture.Rich("Zakázka."), "x"), f.Writer, default);
        await Refused(400, () => f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Tabule", page.Id, "projector.deployment")), "invalid_purpose");
        await Refused(400, () => f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Tabule", null, Board.PagePurpose)), "invalid_parent");
        var boardId = (await f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Tabule nasazení", page.Id, Board.PagePurpose))).Id;
        await Refused(409, () => f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Druhá", page.Id, Board.PagePurpose)), "board_exists");
        await f.ApplyAsync(f.Writer, boardId, Note(Guid.NewGuid(), "Portál"));

        // Unpublished: the page and its board are the owner's alone.
        await Refused(404, () => f.As<BoardDto>(f.Reader, new GetBoard(boardId)));
        Assert.Empty(await FindAsync(f, f.Reader, "portál"));

        await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        var read = await f.As<BoardDto>(f.Reader, new GetBoard(boardId));
        Assert.Equal(("Tabule nasazení", false, 1), (read.Title, read.CanEdit, read.Items.Count));
        Assert.Equal(boardId, Assert.Single(await FindAsync(f, f.Reader, "portál")).Id);
        var tree = (await f.As<PageTreeDto>(f.Reader, new GetPageTree(f.SpaceId))).Items;
        Assert.Equal((boardId, false), (tree.Single(item => item.Id == page.Id).AttachedBoardId, tree.Any(item => item.Id == boardId)));
        // The views of the relations decide it by the same rule (RelationNodes): its usage, count and neighbourhood, the page attached.
        await f.As<ObjectUsageList>(f.Reader, new GetObjectUsage(boardId));
        await f.As<UsageCount>(f.Reader, new CountObjectUsage(boardId));
        var relations = await f.As<ObjectRelations>(f.Reader, new GetObjectRelations(boardId));
        Assert.True(relations.Nodes.Single(node => node.Id == boardId).Readable);
        Assert.Contains(relations.Edges, edge => edge.Kind == "attached" && edge.SourceId == boardId && edge.TargetId == page.Id);
        await Refused(404, () => f.As<UsageCount>(f.Outsider, new CountObjectUsage(boardId)));
        await Refused(403, () => f.ApplyAsync(f.Reader, boardId, Note(Guid.NewGuid())));
        await Refused(403, () => f.As<BoardDto>(f.Reader, new CreateBoard(f.SpaceId, "Moje", page.Id, Board.PagePurpose)));
        Assert.True((await f.As<BoardDto>(f.Writer, new GetBoard(boardId))).CanEdit);
        await Refused(404, () => f.As<BoardDto>(f.Outsider, new GetBoard(boardId)));
        Assert.Empty(await FindAsync(f, f.Outsider, "portál"));

        // Another purpose keeps S4-3: the reader of the page neither opens it nor sees the page's mark.
        await f.Host.ExecuteAsync("""UPDATE "Board" SET "Purpose"='projector.other' WHERE "Id"=:id""", boardId);
        await Refused(404, () => f.As<BoardDto>(f.Reader, new GetBoard(boardId)));
        Assert.Null((await f.As<PageTreeDto>(f.Reader, new GetPageTree(f.SpaceId))).Items.Single(item => item.Id == page.Id).AttachedBoardId);
        Assert.Empty(await FindAsync(f, f.Reader, "portál"));
        await Refused(404, () => f.As<ObjectUsageList>(f.Reader, new GetObjectUsage(boardId)));
        await Refused(404, () => f.As<UsageCount>(f.Reader, new CountObjectUsage(boardId)));
        await Refused(404, () => f.As<ObjectRelations>(f.Reader, new GetObjectRelations(boardId)));
        Assert.True((await f.As<BoardDto>(f.Writer, new GetBoard(boardId))).CanEdit);
        await f.As<UsageCount>(f.Writer, new CountObjectUsage(boardId));
    }

    /// <summary>M1: a space board under a page with its own access is that page's too, wherever it is decided: GET, its log, the list, the
    /// tree, search, the relations and the live channel. The page's readers read it, its editors edit it; a space role is still needed.
    /// Own access turned on after the board was placed applies at once; a page moved under a page with own access keeps its own access
    /// (copy-on-create, WP3 S4), and its board follows it.</summary>
    [PostgreSqlFact]
    public async Task A_board_under_a_page_with_own_access_is_read_only_by_the_pages_readers()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Platy", IndexFixture.Rich("Mzdy."), "x"), f.Writer, default);
        await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        var boardId = (await f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Mzdová tabule", page.Id))).Id;
        await f.ApplyAsync(f.Writer, boardId, Note(Guid.NewGuid(), "odměny vedení"));
        Assert.Equal(boardId, (await f.As<BoardDto>(f.Reader, new GetBoard(boardId))).Id);

        // Own access after the board was placed: the reader has no page role, the space writer only reads the page.
        await f.Host.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=:id""", page.Id);
        f.Host.Actor.Current = f.Admin;
        await f.Host.InvokeAsync(new Atlas.Server.Features.PageAccess.Commands.SetPageGrant(page.Id, new(f.Writer.Id, null, "Reader", null)));
        await f.Host.InvokeAsync(new Atlas.Server.Features.PageAccess.Commands.SetPageGrant(page.Id, new(f.Outsider.Id, null, "Writer", null)));

        await Refused(404, () => f.As<BoardDto>(f.Reader, new GetBoard(boardId)));
        await Refused(404, () => f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 0)));
        await Refused(404, () => f.ApplyAsync(f.Reader, boardId, Note(Guid.NewGuid())));
        Assert.Empty((await f.As<BoardList>(f.Reader, new ListBoards(f.SpaceId))).Boards);
        Assert.DoesNotContain((await f.As<PageTreeDto>(f.Reader, new GetPageTree(f.SpaceId))).Items, item => item.Id == boardId);
        Assert.Empty(await FindAsync(f, f.Reader, "odměny"));
        await Refused(404, () => f.As<UsageCount>(f.Reader, new CountObjectUsage(boardId)));
        Assert.Equal(LiveAccess.NotFound, await Subscriber(f, f.Reader, boardId));
        // A page grant without a space role is no way to a space board.
        await Refused(404, () => f.As<BoardDto>(f.Outsider, new GetBoard(boardId)));
        Assert.Equal(LiveAccess.NotFound, await Subscriber(f, f.Outsider, boardId));

        // The page's reader reads it and may not edit it, though a Writer of the space; the WikiAdmin edits it.
        Assert.False((await f.As<BoardDto>(f.Writer, new GetBoard(boardId))).CanEdit);
        await Refused(403, () => f.ApplyAsync(f.Writer, boardId, Note(Guid.NewGuid())));
        Assert.Equal(new LiveAccess(LiveAccessOutcome.Allowed, false, 1), await Subscriber(f, f.Writer, boardId));
        Assert.Equal([boardId], (await f.As<BoardList>(f.Writer, new ListBoards(f.SpaceId))).Boards.Select(board => board.Id));
        // The tree says what the server allows: read-only for the page's reader, editable for the WikiAdmin.
        Assert.False((await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId))).Items.Single(item => item.Id == boardId).CanEdit);
        Assert.True((await f.As<PageTreeDto>(f.Admin, new GetPageTree(f.SpaceId))).Items.Single(item => item.Id == boardId).CanEdit);
        Assert.Equal(boardId, Assert.Single(await FindAsync(f, f.Writer, "odměny")).Id);
        Assert.True((await f.As<ObjectRelations>(f.Writer, new GetObjectRelations(boardId))).Nodes.Single(node => node.Id == boardId).Readable);
        Assert.True((await f.As<BoardDto>(f.Admin, new GetBoard(boardId))).CanEdit);
        await f.ApplyAsync(f.Admin, boardId, Note(Guid.NewGuid()));

        // A page moved under the page with own access keeps its access, and so does its board: as readable as the page.
        var open = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Veřejné", IndexFixture.Rich("Text."), "x"), f.Writer, default);
        await pages.PublishAsync(open.Id, new PublishPageRequest(open.Version), f.Writer, default);
        var openBoard = (await f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Veřejná tabule", open.Id))).Id;
        var version = (await f.As<PageTreeDto>(f.Admin, new GetPageTree(f.SpaceId))).Version;
        await f.As<Atlas.Server.Features.PageOrganization.Contracts.OrganizationChange>(f.Admin,
            new Atlas.Server.Features.PageOrganization.Commands.MovePage(open.Id, new Atlas.Server.Features.PageOrganization.Contracts.MovePageRequest(page.Id, null, version)));
        Assert.Equal(open.Id, (await pages.GetAsync(open.Id, f.Reader, default)).Id);
        Assert.Equal(openBoard, (await f.As<BoardDto>(f.Reader, new GetBoard(openBoard))).Id);
        // Own access on the moved page hides its board as well.
        await f.Host.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=:id""", open.Id);
        await Refused(404, () => f.As<BoardDto>(f.Reader, new GetBoard(openBoard)));
    }

    private static async Task<IReadOnlyList<SearchResourceHit>> FindAsync(BoardsFixture f, UserAccount actor, string query)
    {
        return (await f.As<SearchResponse>(actor, new Atlas.Server.Features.Search.Queries.Search(query, null, 0, 20, new SearchOptions()))).Resources ?? [];
    }

    /// <summary>A page card shows what its reader may see of the page (interaction spec §3.6, §4): the title, the space and an
    /// excerpt of the published revision for a reader, of the working one for who may edit it; nothing for who may not open it.</summary>
    [PostgreSqlFact]
    public async Task A_page_card_shows_its_title_space_and_excerpt_only_to_who_may_open_the_page_and_needs_read_access_to_be_placed()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        // Elsewhere is a space only the WikiAdmin reaches; the published page of the board's own space is readable by the reader.
        var hidden = await pages.CreateAsync(new CreatePageRequest(f.OtherSpaceId, null, "Secret plan", IndexFixture.Rich("Only the admin reads this."), "x"), f.Admin, default);
        var shared = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Shared notes", IndexFixture.Rich("Published words for every reader."), "x"), f.Writer, default);
        await pages.PublishAsync(shared.Id, new PublishPageRequest(shared.Version), f.Writer, default);
        var draft = await pages.GetAsync(shared.Id, f.Writer, default);
        await pages.SaveAsync(shared.Id, new SavePageRequest("Shared notes, reworked", IndexFixture.Rich("A working draft only its writers see. " + string.Concat(Enumerable.Repeat("More words follow here. ", 30))), null, draft.Version), f.Writer, default);
        var boardId = await f.CreateBoardAsync(f.Admin);
        Guid hiddenCard = Guid.NewGuid(), sharedCard = Guid.NewGuid();
        await f.ApplyAsync(f.Admin, boardId, PageCard(hiddenCard, hidden.Id), PageCard(sharedCard, shared.Id));

        var asReader = (await f.As<BoardDto>(f.Reader, new GetBoard(boardId))).Items.ToDictionary(item => item.Id, item => item.Target);
        Assert.Equal(new BoardTargetDto("atlas.page", hidden.Id, null, null, null, false), asReader[hiddenCard]);
        Assert.Equal(new BoardTargetDto("atlas.page", shared.Id, "Shared notes", "Boards", "Published words for every reader.", true), asReader[sharedCard]);
        var asWriter = (await f.As<BoardDto>(f.Writer, new GetBoard(boardId))).Items.Single(item => item.Id == sharedCard).Target!;
        Assert.Equal(("Shared notes, reworked", "Boards", true, (bool?)true), (asWriter.Title, asWriter.SpaceTitle, asWriter.Available, asWriter.CanEdit));
        Assert.StartsWith("A working draft only its writers see. More words", asWriter.Excerpt);
        Assert.True(asWriter.Excerpt!.Length <= BoardPageTargets.ExcerptLength + 1);
        Assert.EndsWith("…", asWriter.Excerpt);
        var asAdmin = (await f.As<BoardDto>(f.Admin, new GetBoard(boardId))).Items.ToDictionary(item => item.Id, item => item.Target);
        Assert.Equal(new BoardTargetDto("atlas.page", hidden.Id, "Secret plan", "Elsewhere", "Only the admin reads this.", true) { CanEdit = true }, asAdmin[hiddenCard]);

        // Placing a card needs Read on the page: an unreadable page, a missing one and a board (not a page) all look the same.
        foreach (var target in new[] { hidden.Id, Guid.NewGuid(), boardId })
        {
            await Refused(400, () => f.ApplyAsync(f.Writer, boardId, PageCard(Guid.NewGuid(), target)), BoardRejection.Invalid);
        }
        Assert.Equal(1L, (await f.As<BoardDto>(f.Writer, new GetBoard(boardId))).Sequence);

        // A page in the trash has no content for anyone.
        await f.Host.ExecuteAsync("""
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"="OwnerId","TrashBatchId"=gen_random_uuid() WHERE "Id"=:id
            """, hidden.Id);
        var trashed = (await f.As<BoardDto>(f.Admin, new GetBoard(boardId))).Items.Single(item => item.Id == hiddenCard);
        // Who could read it is told it is in the trash (its card is no locked one); a reader who could not open it sees what any unreadable page shows.
        Assert.Equal((false, true), (trashed.Target!.Available, trashed.Target.Trashed));
        Assert.Equal(BoardTargetDto.Unavailable(hidden.Id), (await f.As<BoardDto>(f.Reader, new GetBoard(boardId))).Items.Single(item => item.Id == hiddenCard).Target);
    }

    /// <summary>The excerpt is the page's readable text, never its Markdown: a page made from Markdown with a table, a list,
    /// emphasis and a link shows their words only.</summary>
    [PostgreSqlFact]
    public async Task A_page_card_excerpt_is_the_readable_text_of_a_page_with_a_table_a_list_emphasis_and_a_link()
    {
        await using var f = await BoardsFixture.SharedAsync();
        const string markdown = """
            Kontaktní osoby **projektu**, viz [příručka](https://example.com/guide).

            | Role | Jméno |
            | --- | --- |
            | Vedoucí | Jana Nová |

            - první bod
            - druhý bod
            """;
        f.Host.Actor.Current = f.Writer;
        PageDto page;
        using (var scope = f.Host.Services.CreateScope())
        {
            page = await new PageMarkdownCreate(scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>())
                .CreateAsync(new ApiCreatePageRequest(f.SpaceId, null, "Tým", markdown), default);
        }
        await new TestPages(f.Host).PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        var boardId = await f.CreateBoardAsync();
        var card = Guid.NewGuid();
        await f.ApplyAsync(f.Writer, boardId, PageCard(card, page.Id));

        var target = (await f.As<BoardDto>(f.Reader, new GetBoard(boardId))).Items.Single(item => item.Id == card).Target!;
        Assert.Equal("Kontaktní osoby projektu, viz příručka. Role Jméno Vedoucí Jana Nová první bod druhý bod", target.Excerpt);
    }

    [PostgreSqlFact]
    public async Task A_retried_operation_answers_its_sequence_and_its_id_cannot_carry_other_changes()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        var operation = Guid.NewGuid();
        var item = Guid.NewGuid();
        BoardChange[] changes = [Note(item, "once")];
        Assert.Equal(new BoardBatchResult(1, false), await f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(boardId, operation, changes)));
        // The same changes rebuilt from scratch: a retry after a lost response.
        Assert.Equal(new BoardBatchResult(1, true), await f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(boardId, operation, [Note(item, "once")])));
        await Refused(409, () => f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(boardId, operation, [Note(item, "twice")])), "operation_conflict");
        // Another board has its own operation ids.
        var otherBoard = await f.CreateBoardAsync();
        Assert.Equal(new BoardBatchResult(1, false), await f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(otherBoard, operation, [Note(Guid.NewGuid())])));

        var log = await f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 0));
        Assert.Equal(1L, log.Sequence);
        var batch = Assert.Single(log.Batches);
        Assert.Equal((1L, operation, f.Writer.Id), (batch.Sequence, batch.OperationId, batch.AuthorId));
        Assert.Equal("once", Assert.Single(batch.Changes).Item!.Text);
        Assert.Empty((await f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 1))).Batches);

        await Refused(400, () => f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(boardId, null, changes)), BoardRejection.Invalid);
        await Refused(400, () => f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(boardId, Guid.NewGuid(), [])), BoardRejection.Invalid);
        await Refused(400, () => f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, -1)));
    }

    /// <summary>A retry that overtakes its own lost response: both requests wait on the board row, the first applies, the second
    /// then finds the operation and answers its sequence.</summary>
    [PostgreSqlFact]
    public async Task Two_concurrent_requests_with_one_operation_id_apply_it_once_and_both_answer_its_sequence()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        f.Subscribe(f.Reader, boardId);
        var operation = Guid.NewGuid();
        var item = Guid.NewGuid();
        await using var other = new NpgsqlConnection(f.Host.ConnectionString);
        await other.OpenAsync();
        await using var blocking = await other.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("""SELECT 1 FROM "Board" WHERE "Id"=@id FOR UPDATE""", other, blocking))
        {
            hold.Parameters.AddWithValue("id", boardId);
            await hold.ExecuteNonQueryAsync();
        }
        var requests = Enumerable.Range(0, 2).Select(_ => f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(boardId, operation, [Note(item, "once")]))).ToArray();
        await WaitForLockWaitAsync(f.Host.ConnectionString, other, Task.WhenAll(requests), 2);
        await blocking.CommitAsync();

        Assert.Equal([new BoardBatchResult(1, false), new BoardBatchResult(1, true)], (await Task.WhenAll(requests)).OrderBy(result => result.Duplicate));
        Assert.Single((await f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 0))).Batches);
        Assert.Single(f.Hub.Sent);
    }

    [PostgreSqlFact]
    public async Task The_log_and_the_broadcast_carry_the_changes_as_applied_and_a_retry_of_the_raw_request_is_recognized()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        f.Subscribe(f.Reader, boardId);
        var item = Guid.NewGuid();
        await f.ApplyAsync(f.Writer, boardId, Note(item));
        var operation = Guid.NewGuid();
        // A title the server trims, and members a move does not use: neither is what the server applied.
        BoardChange[] raw = [new("rename", Title: "  Plán  "), new("move", ItemId: item, X: 5, Y: 6, Width: 160, Height: 120, Text: "ignored", Title: "ignored")];
        Assert.Equal(new BoardBatchResult(2, false), await f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(boardId, operation, raw)));

        BoardChange[] applied = [new("rename", Title: "Plán"), new("move", ItemId: item, X: 5, Y: 6, Width: 160, Height: 120)];
        // Stored as UTF-8 text, not as \u escapes: the byte budget of the catch-up counts what is really there.
        using (var session = f.Host.OpenSession())
        {
            var stored = await session.QueryOver<BoardOperation>().Where(x => x.BoardId == boardId && x.Sequence == 2).SingleOrDefaultAsync();
            Assert.Equal($$"""[{"type":"rename","title":"Plán"},{"type":"move","itemId":"{{item}}","x":5,"y":6,"width":160,"height":120}]""", stored.ChangesJson);
        }
        Assert.Equal(applied, Assert.Single((await f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 1))).Batches).Changes);
        Assert.Equal(applied, Assert.IsType<BoardChangedMessage>(f.Hub.Sent[^1].Payload).Changes);
        // A retry of the raw request is the same operation, and so is the same gesture sent already normalized.
        Assert.Equal(new BoardBatchResult(2, true), await f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(boardId, operation, raw)));
        Assert.Equal(new BoardBatchResult(2, true), await f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(boardId, operation, applied)));
        await Refused(409, () => f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(boardId, operation, [new("rename", Title: "Other")])), "operation_conflict");
    }

    /// <summary>Props and settings through the handlers and jsonb (interaction spec §4): every kind keeps its props, a partial set
    /// merges per key, the log and the broadcast carry exactly the keys a change named (null included, the shape an undo needs),
    /// and a retry with the keys in another order is the same operation.</summary>
    [PostgreSqlFact]
    public async Task Props_and_settings_round_trip_through_the_store_and_reach_the_log_and_the_broadcast_canonically()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        f.Subscribe(f.Reader, boardId);
        Guid note = Guid.NewGuid(), text = Guid.NewGuid(), block = Guid.NewGuid(), link = Guid.NewGuid();
        const string contact = """{"block":{"attrs":{"data":{"contacts":[{"emails":["jana@example.com"],"name":"Jana Nováková"}],"version":1}},"type":"contactCard"}}""";
        await f.ApplyAsync(f.Writer, boardId, Note(note),
            new BoardChange("create", Item: new BoardItemInput(text, "text", 0, 200, 300, 40, 1, Text: "**Plán**", Props: Json("""{"fontSize":"m","bold":true,"align":"center"}"""))),
            new BoardChange("create", Item: new BoardItemInput(block, "block", 400, 0, 240, 160, 2, Props: Json(contact))),
            new BoardChange("create", Item: new BoardItemInput(link, "connector", 0, 0, 8, 8, 3, Text: "závisí na", From: note, To: text, Props: Elbow())),
            new BoardChange("settings", Settings: Json("""{"snapToGrid":true,"gridSize":16}""")));

        var read = await f.As<BoardDto>(f.Reader, new GetBoard(boardId));
        Assert.Equal("""{"background":"dots","gridSize":16,"snapToGrid":true}""", read.Settings.GetRawText());
        var props = read.Items.ToDictionary(item => item.Id, item => item.Props.GetRawText());
        Assert.Equal(("{}", """{"align":"center","bold":true,"fontSize":"m"}""", contact, """{"arrowEnd":"arrow","arrowStart":"none","dash":"solid","route":"elbow"}"""),
            (props[note], props[text], props[block], props[link]));

        var operation = Guid.NewGuid();
        var undoable = new BoardChange("set", ItemId: text, Props: Json("""{"fontSize":"xl","bold":null}"""));
        Assert.Equal(new BoardBatchResult(2, false), await f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(boardId, operation, [undoable])));
        Assert.Equal("""{"align":"center","fontSize":"xl"}""", (await f.As<BoardDto>(f.Reader, new GetBoard(boardId))).Items.Single(item => item.Id == text).Props.GetRawText());
        var logged = Assert.Single(Assert.Single((await f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 1))).Batches).Changes);
        Assert.Equal("""{"bold":null,"fontSize":"xl"}""", BoardJson.Write(logged.Props!));
        Assert.Equal("""{"bold":null,"fontSize":"xl"}""", BoardJson.Write(Assert.Single(Assert.IsType<BoardChangedMessage>(f.Hub.Sent[^1].Payload).Changes).Props!));
        var retry = new BoardChange("set", ItemId: text, Props: Json("""{"bold":null,"fontSize":"xl"}"""));
        Assert.Equal(new BoardBatchResult(2, true), await f.As<BoardBatchResult>(f.Writer, new ApplyBoardBatch(boardId, operation, [retry])));

        // The settings are the board's: a reader may not change them, an invalid one is refused.
        await Refused(403, () => f.ApplyAsync(f.Reader, boardId, new BoardChange("settings", Settings: Json("""{"background":"none"}"""))));
        await Refused(400, () => f.ApplyAsync(f.Writer, boardId, new BoardChange("settings", Settings: Json("""{"gridSize":7}"""))), BoardRejection.Invalid);
    }

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>Membership is stored (objects spec §5.4): a reader sees each member's parent, the log and the broadcast carry the
    /// <c>parent</c> change, and the depth rule counts the links already stored, not only the ones of the batch.</summary>
    [PostgreSqlFact]
    public async Task Group_membership_round_trips_through_the_store_and_its_depth_counts_the_stored_links()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        var groups = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        Guid note = Guid.NewGuid(), deeper = Guid.NewGuid();
        await f.ApplyAsync(f.Writer, boardId, [
            ..groups.Select((id, index) => new BoardChange("create", Item: new BoardItemInput(id, "group", 0, 0, 400, 300, index, Text: $"Skupina {index}"))),
            new BoardChange("create", Item: new BoardItemInput(deeper, "group", 0, 0, 100, 100, 9)),
            Note(note),
            ..groups.Skip(1).Select((id, index) => new BoardChange("parent", ItemId: id, ParentId: groups[index]))]);
        f.Subscribe(f.Reader, boardId);

        await f.ApplyAsync(f.Writer, boardId, new BoardChange("parent", ItemId: note, ParentId: groups[3]));
        var read = (await f.As<BoardDto>(f.Reader, new GetBoard(boardId))).Items.ToDictionary(item => item.Id);
        Assert.Equal((groups[3], groups[0], (Guid?)null), (read[note].Parent, read[groups[1]].Parent, read[groups[0]].Parent));
        var sent = Assert.Single(Assert.IsType<BoardChangedMessage>(Assert.Single(f.Hub.Sent).Payload).Changes);
        Assert.Equal(new BoardChange("parent", ItemId: note, ParentId: groups[3]), sent);

        // groups[3] already has three ancestors in the store: a group with a member under it would be five levels down.
        await f.ApplyAsync(f.Writer, boardId, new BoardChange("parent", ItemId: note, ParentId: deeper));
        await Refused(400, () => f.ApplyAsync(f.Writer, boardId, new BoardChange("parent", ItemId: deeper, ParentId: groups[3])), BoardRejection.Invalid);
        await Refused(400, () => f.ApplyAsync(f.Writer, boardId, new BoardChange("parent", ItemId: groups[0], ParentId: groups[2])), BoardRejection.Invalid);
        await f.ApplyAsync(f.Writer, boardId, new BoardChange("parent", ItemId: note));
        Assert.Null((await f.As<BoardDto>(f.Reader, new GetBoard(boardId))).Items.Single(item => item.Id == note).Parent);
    }

    [PostgreSqlFact]
    public async Task A_refused_batch_leaves_the_board_as_it_was_and_announces_nothing()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        f.Subscribe(f.Reader, boardId);
        var existing = Guid.NewGuid();
        await f.ApplyAsync(f.Writer, boardId, Note(existing, "kept"));
        Assert.Single(f.Hub.Sent);

        await Refused(409, () => f.ApplyAsync(f.Writer, boardId, new BoardChange("rename", Title: "Lost"), Note(Guid.NewGuid()),
            new BoardChange("set", ItemId: existing, Text: "lost"), new BoardChange("set", ItemId: Guid.NewGuid(), Text: "nobody")), BoardRejection.ItemMissing);
        await Refused(409, () => f.ApplyAsync(f.Writer, boardId, Note(existing)), BoardRejection.ItemExists);
        await f.ApplyAsync(f.Writer, boardId, new BoardChange("delete", ItemId: existing));
        await Refused(409, () => f.ApplyAsync(f.Writer, boardId, new BoardChange("set", ItemId: existing, Text: "x")), BoardRejection.ItemDeleted);

        var board = await f.As<BoardOperationsDto>(f.Writer, new GetBoardOperations(boardId, 0));
        Assert.Equal([1L, 2L], board.Batches.Select(batch => batch.Sequence));
        Assert.Equal(2, f.Hub.Sent.Count);
        using var session = f.Host.OpenSession();
        var item = await session.GetAsync<BoardItem>(existing);
        Assert.Equal(("kept", 2L, true), (item.Text, item.Version, item.IsDeleted));
        Assert.Equal("Plan", (await session.GetAsync<Board>(boardId)).Title);
    }

    /// <summary>Batches on one board take its row FOR UPDATE, so they serialize: every one commits with its own sequence, none is
    /// lost, the log has no gap, and the item they all set ends with the value of the last one in that order.</summary>
    [PostgreSqlFact]
    public async Task Concurrent_batches_on_one_board_serialize_without_gaps_or_lost_updates()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        var shared = Guid.NewGuid();
        await f.ApplyAsync(f.Writer, boardId, Note(shared));
        var writers = new[] { f.Writer, f.Admin };
        var batches = Enumerable.Range(0, 12).Select(index => (Item: Guid.NewGuid(), Text: "writer " + index)).ToArray();

        var results = await Task.WhenAll(batches.Select((batch, index) => f.ApplyAsync(writers[index % 2], boardId,
            Note(batch.Item), new BoardChange("set", ItemId: shared, Text: batch.Text))));

        Assert.Equal(Enumerable.Range(2, 12).Select(sequence => (long)sequence), results.Select(result => result.Sequence).Order());
        var log = await f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 0));
        Assert.Equal(13L, log.Sequence);
        Assert.Equal(Enumerable.Range(1, 13).Select(sequence => (long)sequence), log.Batches.Select(batch => batch.Sequence));
        var board = await f.As<BoardDto>(f.Reader, new GetBoard(boardId));
        Assert.Equal(13, board.Items.Count);
        var last = log.Batches[^1].Changes.Single(change => change.Type == "set").Text;
        var sharedItem = board.Items.Single(item => item.Id == shared);
        Assert.Equal((last, 13L), (sharedItem.Text, sharedItem.Version));
    }

    [PostgreSqlFact]
    public async Task BoardChanged_goes_out_after_the_commit_and_a_batch_rolled_back_at_commit_sends_nothing()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        var otherBoard = await f.CreateBoardAsync();
        var subscriber = f.Subscribe(f.Reader, boardId);
        f.Subscribe(f.Reader, otherBoard);
        long? committedWhileSending = null;
        f.Hub.OnSend = async _ =>
        {
            // Another connection: it sees only what is committed.
            await using var connection = new NpgsqlConnection(f.Host.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""SELECT "Sequence" FROM "Board" WHERE "Id"=@id""", connection);
            command.Parameters.AddWithValue("id", boardId);
            committedWhileSending = (long)(await command.ExecuteScalarAsync())!;
        };
        var item = Guid.NewGuid();
        await f.ApplyAsync(f.Writer, boardId, Note(item, "hello"));
        var sent = Assert.Single(f.Hub.Sent);
        Assert.Equal((subscriber, "BoardChanged", 1L), (Assert.Single(sent.Connections), sent.Method, committedWhileSending));
        var message = Assert.IsType<BoardChangedMessage>(sent.Payload);
        Assert.Equal((boardId, 1L, f.Writer.Id), (message.BoardId, message.Sequence, message.AuthorId));
        Assert.Equal(item, Assert.Single(message.Changes).Item!.Id);

        // The batch passes its checks, but its new item id is being inserted on another board by a transaction that commits
        // first: the insert fails when this batch commits, the transaction rolls back and nothing is announced.
        f.Hub.OnSend = null;
        var collision = Guid.NewGuid();
        await using var other = new NpgsqlConnection(f.Host.ConnectionString);
        await other.OpenAsync();
        await using var blocking = await other.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Version","IsDeleted") VALUES(@id,@board,'note',0,0,8,8,0,1,false)
            """, other, blocking))
        {
            insert.Parameters.AddWithValue("id", collision);
            insert.Parameters.AddWithValue("board", otherBoard);
            await insert.ExecuteNonQueryAsync();
        }
        var batch = f.ApplyAsync(f.Writer, boardId, Note(collision));
        await WaitForLockWaitAsync(f.Host.ConnectionString, other, batch);
        await blocking.CommitAsync();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => batch);
        for (; failure is not PostgresException && failure.InnerException is { } inner; failure = inner)
        {
        }
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure).SqlState);

        Assert.Single(f.Hub.Sent);
        Assert.Equal(1L, (await f.As<BoardOperationsDto>(f.Writer, new GetBoardOperations(boardId, 0))).Sequence);
        Assert.DoesNotContain((await f.As<BoardDto>(f.Writer, new GetBoard(boardId))).Items, x => x.Id == collision);
    }

    /// <summary>Review S1: the log and BoardChanged follow GET's rule for who may not write. A removed item's create and sets never
    /// reach a reader (the removed items are a writer's view), nothing of an item whose create it was not told, while every batch
    /// keeps its sequence; a live card to a page it cannot read shows what GET shows. A writer hears and reads it all.</summary>
    [PostgreSqlFact]
    public async Task A_reader_neither_reads_nor_hears_the_text_of_removed_items_while_a_writer_gets_the_whole_log()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var hidden = await new TestPages(f.Host).CreateAsync(new CreatePageRequest(f.OtherSpaceId, null, "Secret plan", IndexFixture.Rich("x"), "x"), f.Admin, default);
        var boardId = await f.CreateBoardAsync(f.Admin);
        var reader = f.Subscribe(f.Reader, boardId);
        var writer = f.Subscribe(f.Writer, boardId, canWrite: true);
        Guid kept = Guid.NewGuid(), gone = Guid.NewGuid(), flash = Guid.NewGuid(), card = Guid.NewGuid(), goneCard = Guid.NewGuid();
        await f.ApplyAsync(f.Admin, boardId, Note(kept, "visible"), Note(gone, "secret one"), PageCard(card, hidden.Id), PageCard(goneCard, hidden.Id));
        // Everything is visible after the first batch: one message for all.
        Assert.Equal(new[] { reader, writer }.Order(), Assert.Single(f.Hub.Sent).Connections.Order());
        await f.ApplyAsync(f.Admin, boardId, new BoardChange("set", ItemId: gone, Text: "secret two"), new BoardChange("delete", ItemId: gone),
            new BoardChange("delete", ItemId: goneCard), Note(flash, "secret three"), new BoardChange("delete", ItemId: flash));

        // Live: the reader held gone and goneCard, so it hears them go, but not the new text nor the note made and removed at once.
        var live = f.Hub.Sent.Skip(1).ToDictionary(sent => Assert.Single(sent.Connections), sent => Assert.IsType<BoardChangedMessage>(sent.Payload).Changes);
        Assert.Equal([new BoardChange("delete", ItemId: gone), new BoardChange("delete", ItemId: goneCard)], live[reader]);
        Assert.Equal(5, live[writer].Count);

        var asReader = await f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 0));
        Assert.Equal([1L, 2L], asReader.Batches.Select(batch => batch.Sequence));
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(asReader));
        // From the start it never heard of the removed items; the live card names its target as GET does (no title, no excerpt).
        Assert.Equal([kept, card], asReader.Batches[0].Changes.Select(change => change.Item!.Id));
        Assert.Empty(asReader.Batches[1].Changes);
        var shown = (await f.As<BoardDto>(f.Reader, new GetBoard(boardId))).Items.Single(item => item.Id == card).Target!;
        Assert.Equal((shown.ResourceId, false), (asReader.Batches[0].Changes[1].Item!.TargetResourceId!.Value, shown.Available));
        // Caught up from after the first batch, it holds the removed items, so it is told they went.
        Assert.Equal(live[reader], Assert.Single((await f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 1))).Batches).Changes);

        var asWriter = await f.As<BoardOperationsDto>(f.Writer, new GetBoardOperations(boardId, 0));
        Assert.Equal((4, 5), (asWriter.Batches[0].Changes.Count, asWriter.Batches[1].Changes.Count));
        Assert.Contains("secret two", System.Text.Json.JsonSerializer.Serialize(asWriter));

        // L1: text a later batch replaced on a kept item reaches the writers' log only; the reader catches up to the text as it is.
        await f.ApplyAsync(f.Admin, boardId, new BoardChange("set", ItemId: kept, Text: "heslo: tajne123"));
        await f.ApplyAsync(f.Admin, boardId, new BoardChange("set", ItemId: kept, Text: "bez hesla"));
        var caughtUp = System.Text.Json.JsonSerializer.Serialize(await f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 0)));
        Assert.DoesNotContain("tajne123", caughtUp);
        Assert.Contains("bez hesla", caughtUp);
        Assert.Contains("tajne123", System.Text.Json.JsonSerializer.Serialize(await f.As<BoardOperationsDto>(f.Writer, new GetBoardOperations(boardId, 0))));
    }

    /// <summary>COL2-3: a participant that takes long to receive (a browser swamped by 30 colleagues) held every writer's answer.
    /// The answers come at once; the board's messages follow in the order of their sequences once the slow send lets go.</summary>
    [PostgreSqlFact]
    public async Task A_batch_answers_without_waiting_for_its_live_messages_which_follow_in_sequence_order()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        f.Subscribe(f.Reader, boardId);
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Hub.OnSend = _ => slow.Task;

        f.Host.Actor.Current = f.Writer;
        var answers = new List<long>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                // Not f.ApplyAsync, which waits for the messages: the answer alone, while the first send hangs.
                var answer = await f.Host.InvokeAsync<BoardBatchResult>(new ApplyBoardBatch(boardId, Guid.NewGuid(), [Note(Guid.NewGuid(), $"note {i}")])).WaitAsync(TimeSpan.FromSeconds(10));
                answers.Add(answer.Sequence);
            }
            Assert.Equal([1L, 2L, 3L], answers);
            Assert.Empty(f.Hub.Sent);
        }
        finally
        {
            // Also when this test fails: BoardBroadcasts.IdleAsync is host-wide, so a send left hanging stalls every test of the shared host.
            slow.TrySetResult();
        }
        await f.Broadcasts.IdleAsync();
        Assert.Equal([1L, 2L, 3L], f.Hub.Sent.Where(sent => sent.Method == "BoardChanged").Select(sent => ((BoardChangedMessage)sent.Payload!).Sequence));
    }

    [PostgreSqlFact]
    public async Task A_trashed_board_is_gone_for_everyone_and_its_subscribers_are_revoked()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        var kept = await f.CreateBoardAsync(title: "Kept");
        var subscriber = f.Subscribe(f.Reader, boardId);
        await f.As(f.Writer, new DeleteBoard(boardId));

        Assert.Equal("Revoked", Assert.Single(f.Hub.Sent).Method);
        Assert.Equal(new LiveRevoked("board:" + boardId), f.Hub.Sent[0].Payload);
        Assert.Null(f.Subscriptions.CanWrite(subscriber, "board:" + boardId));
        await Refused(404, () => f.As<BoardDto>(f.Writer, new GetBoard(boardId)));
        await Refused(404, () => f.ApplyAsync(f.Writer, boardId, Note(Guid.NewGuid())));
        await Refused(404, () => f.As<BoardOperationsDto>(f.Writer, new GetBoardOperations(boardId, 0)));
        await Refused(404, () => f.As(f.Writer, new DeleteBoard(boardId)));
        Assert.Equal([kept], (await f.As<BoardList>(f.Writer, new ListBoards(f.SpaceId))).Boards.Select(board => board.Id));
        Assert.Equal(LiveAccess.NotFound, await Subscriber(f, f.Writer, boardId));
    }

    [PostgreSqlFact]
    public async Task The_operation_log_answers_resync_required_past_500_batches()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        // 501 committed batches, written directly: the handler path is covered above, this is about the cap.
        await f.Host.ExecuteAsync("""
            INSERT INTO "BoardOperation"("BoardId","Sequence","OperationId","AuthorId","AppliedAt","ChangesJson")
              SELECT :id, s, gen_random_uuid(), "CreatedBy", now(), '[{"type":"rename","title":"x"}]' FROM "Board", generate_series(1,501) s WHERE "Id"=:id;
            UPDATE "Board" SET "Sequence"=501 WHERE "Id"=:id;
            """, boardId);
        await Refused(409, () => f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 0)), "resync_required");
        var tail = await f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 1));
        Assert.Equal((501L, 500, 2L), (tail.Sequence, tail.Batches.Count, tail.Batches[0].Sequence));
    }

    /// <summary>The answer carries at most MaxBytes of stored changes, all missed batches or none: past it the client reloads,
    /// also when the first batch it missed is that large alone.</summary>
    [PostgreSqlFact]
    public async Task The_operation_log_answers_resync_required_when_the_missed_changes_exceed_the_byte_budget()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        // Written directly, like the cap above: three batches of a third of the budget each, so all three just do not fit.
        await f.Host.ExecuteAsync($$"""
            INSERT INTO "BoardOperation"("BoardId","Sequence","OperationId","AuthorId","AppliedAt","ChangesJson")
              SELECT :id, s, gen_random_uuid(), "CreatedBy", now(), '[{"type":"rename","title":"' || repeat('x', {{GetBoardOperationsHandler.MaxBytes / 3}}) || '"}]'
              FROM "Board", generate_series(1,3) s WHERE "Id"=:id;
            UPDATE "Board" SET "Sequence"=3 WHERE "Id"=:id;
            """, boardId);
        await Refused(409, () => f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 0)), "resync_required");
        Assert.Equal([2L, 3L], (await f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 1))).Batches.Select(batch => batch.Sequence));

        await f.Host.ExecuteAsync($$"""
            INSERT INTO "BoardOperation"("BoardId","Sequence","OperationId","AuthorId","AppliedAt","ChangesJson")
              SELECT :id, 4, gen_random_uuid(), "CreatedBy", now(), '[{"type":"rename","title":"' || repeat('x', {{GetBoardOperationsHandler.MaxBytes}}) || '"}]' FROM "Board" WHERE "Id"=:id;
            UPDATE "Board" SET "Sequence"=4 WHERE "Id"=:id;
            """, boardId);
        await Refused(409, () => f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 3)), "resync_required");
        var current = await f.As<BoardOperationsDto>(f.Reader, new GetBoardOperations(boardId, 4));
        Assert.Equal((4L, 0), (current.Sequence, current.Batches.Count));
    }

    /// <summary>L3: the live items of one board fit BoardEditing.MaxBoardBytes; a batch past it is refused whole and the board stays
    /// as it was, while a batch that frees room (a delete) and one within the budget pass.</summary>
    [PostgreSqlFact]
    public async Task A_batch_that_would_make_the_board_larger_than_its_budget_is_refused()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        // 175 notes of 4000 two-byte characters per batch: two batches fit, the third would pass 4 MiB.
        BoardChange[] Notes() => Enumerable.Range(0, 175).Select(_ => Note(Guid.NewGuid(), new string('ž', BoardEditing.MaxText))).ToArray();
        var first = Notes();
        await f.ApplyAsync(f.Writer, boardId, first);
        await f.ApplyAsync(f.Writer, boardId, Notes());
        await Refused(400, () => f.ApplyAsync(f.Writer, boardId, Notes()), "board_too_large");
        Assert.Equal((2L, 350), await f.As<BoardDto>(f.Writer, new GetBoard(boardId)) is var board ? (board.Sequence, board.Items.Count) : default);

        await f.ApplyAsync(f.Writer, boardId, first.Select(change => new BoardChange("delete", ItemId: change.Item!.Id)).ToArray());
        Assert.Equal(4L, (await f.ApplyAsync(f.Writer, boardId, Notes())).Sequence);
    }

    /// <summary>L3: the search document indexes at most BoardSearch.MaxBodyCharacters, so a board of many distinct words (more than
    /// PostgreSQL's 1 MB tsvector holds) still takes its batch and stays findable by its first texts.</summary>
    [PostgreSqlFact]
    public async Task A_board_of_many_distinct_words_still_takes_a_batch_and_is_found_by_its_first_text()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        // Distinct camel-case identifiers: each word, its stem and its parts are lexemes of their own.
        string Words(int note) => string.Join(' ', Enumerable.Range(0, 200).Select(word => $"q{note}x{word}Kod{note}w{word}"));
        var notes = Enumerable.Range(0, 150).Select(note => Note(Guid.NewGuid(), Words(note))).Prepend(new BoardChange("create", Item: new BoardItemInput(Guid.NewGuid(), "note", 0, 0, 160, 120, -1, Text: "zahajovaci"))).ToArray();
        Assert.True(notes.Sum(note => note.Item!.Text!.Length) > 2 * BoardSearch.MaxBodyCharacters);
        await f.ApplyAsync(f.Writer, boardId, notes);
        Assert.Equal(boardId, Assert.Single(await FindAsync(f, f.Reader, "zahajovaci")).Id);
        await using var connection = new NpgsqlConnection(f.Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""SELECT length("Body") FROM "BoardSearchDocument" WHERE "BoardId"='{boardId}'""", connection);
        Assert.Equal(BoardSearch.MaxBodyCharacters, (int)(await command.ExecuteScalarAsync())!);
    }

    [PostgreSqlFact]
    public async Task A_live_subscriber_is_decided_for_its_handshake_account_and_session()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var boardId = await f.CreateBoardAsync();
        await f.ApplyAsync(f.Writer, boardId, Note(Guid.NewGuid()));
        Assert.Equal(new LiveAccess(LiveAccessOutcome.Allowed, false, 1), await Subscriber(f, f.Reader, boardId));
        Assert.Equal(new LiveAccess(LiveAccessOutcome.Allowed, true, 1), await Subscriber(f, f.Writer, boardId));
        Assert.Equal(new LiveAccess(LiveAccessOutcome.Allowed, true, 1), await Subscriber(f, f.Admin, boardId));
        Assert.Equal(LiveAccess.NotFound, await Subscriber(f, f.Outsider, boardId));
        Assert.Equal(LiveAccess.NotFound, await Subscriber(f, f.Writer, Guid.NewGuid()));
        // A rotated stamp (sign-out everywhere, password change) no longer proves the account.
        Assert.Equal(LiveAccess.Forbidden, await f.Host.InvokeAsync<LiveAccess>(new AuthorizeBoardSubscriber(boardId, f.Reader.Id, "rotated", null)));
        Assert.Equal(LiveAccess.Forbidden, await f.Host.InvokeAsync<LiveAccess>(new AuthorizeBoardSubscriber(boardId, Guid.NewGuid(), "x", null)));
    }

    private static Task<LiveAccess> Subscriber(BoardsFixture f, UserAccount account, Guid boardId)
    {
        return f.Host.InvokeAsync<LiveAccess>(new AuthorizeBoardSubscriber(boardId, account.Id, account.SecurityStamp, null));
    }

    /// <summary>Until <paramref name="waiting"/> backends wait behind a lock <paramref name="blocker"/> holds, directly or queued behind
    /// another waiter (the batch's insert behind the uncommitted one); other tests' lock waits on the shared database do not count.</summary>
    private static async Task WaitForLockWaitAsync(string connectionString, NpgsqlConnection blocker, Task batch, int waiting = 1)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 600 && !batch.IsCompleted; attempt++)
        {
            await using var command = new NpgsqlCommand($"""
                WITH RECURSIVE waiting(pid) AS (
                  SELECT pid FROM pg_stat_activity WHERE {blocker.ProcessID} = ANY(pg_blocking_pids(pid))
                  UNION SELECT a.pid FROM pg_stat_activity a JOIN waiting w ON w.pid = ANY(pg_blocking_pids(a.pid)))
                SELECT count(*) FROM waiting
                """, connection);
            if ((long)(await command.ExecuteScalarAsync())! >= waiting)
            {
                return;
            }
            await Task.Delay(50);
        }
        // A batch that finished without waiting would have failed the assertions after this.
    }
}
