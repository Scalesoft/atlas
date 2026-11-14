using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Queries;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.PersonalLibrary.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Queries;
using Atlas.Server.Features.YouTrack;
using Atlas.Server.Features.YouTrack.Contracts;
using Wolverine;

namespace Atlas.Server.Features.Boards;

/// <summary>The board API (boards spec §6); mapped only with the module on. The web's cookie API under /api (CSRF and Problem Details from
/// the pipeline) and, for AI parity, the same boards and item batches for personal tokens under /api/v1: reads with the read scope, every
/// write with the write scope (a board changes for its readers at once, as a tree move), audited. The rules are the handlers'.</summary>
public static class BoardEndpoints
{
    public static void MapBoards(this IEndpointRouteBuilder app)
    {
        MapShared(app.MapGroup("/api").RequireAuthorization(), false);
        MapShared(app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
        MapWeb(app.MapGroup("/api").RequireAuthorization());
    }

    private static void MapShared(RouteGroupBuilder group, bool personalApi)
    {
        var list = group.MapGet("/spaces/{spaceId:guid}/boards", async (Guid spaceId, IMessageBus bus, CancellationToken ct)
                => Results.Ok((await bus.InvokeAsync<BoardList>(new ListBoards(spaceId), ct)).Boards))
            .Produces<IReadOnlyList<BoardSummaryDto>>()
            .ProducesProblem(404)
            .WithSummary("The space's boards (id, title, updatedAt, itemCount).");
        var create = group.MapPost("/spaces/{spaceId:guid}/boards", async (Guid spaceId, CreateBoardRequest request, IMessageBus bus, CancellationToken ct) =>
            {
                var board = await bus.InvokeAsync<BoardDto>(new CreateBoard(spaceId, request.Title, request.ParentPageId, request.Purpose), ct);
                return Results.Created($"/api/boards/{board.Id}", board);
            })
            .Produces<BoardDto>(201)
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .WithSummary("Create an empty board at the end of the space tree under parentPageId (none: the root). Requires Writer in the space. " +
                "With purpose projector.board, the page's own board instead (not in the tree, one per page, 409 board_exists): requires editing parentPageId.");
        var get = group.MapGet("/boards/{boardId:guid}", async (Guid boardId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<BoardDto>(new GetBoard(boardId), ct)))
            .Produces<BoardDto>()
            .ProducesProblem(404)
            .WithSummary("The board at its sequence: settings, canEdit and the visible items (kind, position, size, z, text, color, props, parent, connector ends, " +
                "what a page or object card shows).");
        var delete = group.MapDelete("/boards/{boardId:guid}", async (Guid boardId, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new DeleteBoard(boardId), ct);
                return Results.NoContent();
            })
            .Produces(204)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .WithSummary("Move the board to the space's trash (untrash brings it back). Requires Writer in the space.");
        var apply = group.MapPost("/boards/{boardId:guid}/operations", async (Guid boardId, BoardBatchRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<BoardBatchResult>(new ApplyBoardBatch(boardId, request.OperationId, request.Changes), ct)))
            .Produces<BoardBatchResult>()
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409)
            .WithSummary("Apply 1–200 item changes all or none, as the canvas does: create {item}, move {itemId, x, y, width, height}, set {itemId, text?, color?, props?}, " +
                "reorder {itemId, z}, parent {itemId, parentId?} (into a group or frame; none takes it out), delete/restore {itemId}, rename {title}, settings {settings}. " +
                "operationId is the retry key. Requires Writer in the space.");
        var operations = group.MapGet("/boards/{boardId:guid}/operations", async (Guid boardId, long after, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<BoardOperationsDto>(new GetBoardOperations(boardId, after), ct)))
            .Produces<BoardOperationsDto>()
            .ProducesProblem(404)
            .ProducesProblem(409)
            .WithSummary("The batches after the sequence ?after= in order, and the board's current sequence.");
        // The board in the space tree (interaction spec §6): the page organization's request and answer shapes.
        var move = group.MapPost("/boards/{boardId:guid}/move", async (Guid boardId, MovePageRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<OrganizationChange>(new MoveBoard(boardId, request), ct)))
            .Produces<OrganizationChange>()
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409)
            .WithSummary("Move the board within its space tree using expectedTreeVersion, as a page move. Requires Writer in the space.");
        var untrash = group.MapPost("/boards/{boardId:guid}/untrash", async (Guid boardId, UntrashPageRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<OrganizationChange>(new UntrashBoard(boardId, request), ct)))
            .Produces<OrganizationChange>()
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409)
            .WithSummary("Bring the board back from the space's trash under parentId (none: the root). Requires Admin in the space.");
        if (personalApi)
        {
            list.WithName("ListBoards");
            get.WithName("GetBoard");
            operations.WithName("GetBoardOperations");
            create.WithName("CreateBoard").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.BoardCreate);
            delete.WithName("DeleteBoard").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.BoardDelete);
            apply.WithName("ApplyBoardOperations").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.BoardEdit);
            move.WithName("MoveBoard").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.BoardMove);
            untrash.WithName("UntrashBoard").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.BoardUntrash);
        }
        else
        {
            // The canvas sends one batch at a time (a drag is one batch on drop); the personal API has its own budget (L4).
            apply.RequireRateLimiting(BoardBatchRateLimit);
        }
    }

    /// <summary>The web's batches per account: 120 at once, then 30 a second, far above one batch per round trip of the canvas.</summary>
    public const string BoardBatchRateLimit = "board-batch";

    /// <summary>The web's own: personal state and the YouTrack block's host service.</summary>
    private static void MapWeb(RouteGroupBuilder app)
    {
        // The board's „Odebrané“: what a restore change can bring back (for AI the same change is in the batch endpoint).
        app.MapGet("/boards/{boardId:guid}/removed", async (Guid boardId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<RemovedBoardItemsDto>(new GetRemovedBoardItems(boardId), ct)))
            .Produces<RemovedBoardItemsDto>()
            .ProducesProblem(403)
            .ProducesProblem(404);
        // Personal state, as a page's: the same favorites and recent visits (PersonalLibrary), decided through BoardContent.
        app.MapGet("/boards/{boardId:guid}/personal-state", async (Guid boardId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<PersonalPageState>(new GetPersonalPageState(boardId), ct)))
            .Produces<PersonalPageState>()
            .ProducesProblem(404);
        app.MapPut("/boards/{boardId:guid}/favorite", async (Guid boardId, SetFavorite request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<PersonalPageState>(new SetPageFavorite(boardId, request), ct)))
            .Produces<PersonalPageState>()
            .ProducesProblem(404);
        app.MapPost("/boards/{boardId:guid}/visit", async (Guid boardId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<PersonalPageState>(new RecordPageVisit(boardId), ct)))
            .Produces<PersonalPageState>()
            .ProducesProblem(404);
        // The editor's youtrack block on a board: the page's YouTrack host service with the board's space (its project mapping and
        // read cache). The board is decided in its own short transaction, before the runner's; YouTrack is called with none open.
        app.MapGet("/boards/{boardId:guid}/youtrack/search", async (Guid boardId, string q, IMessageBus bus, YouTrackReadRunner runner, CancellationToken ct)
                => await YouTrackEndpoints.SearchAsync(runner, (await bus.InvokeAsync<BoardSpace>(new GetBoardSpace(boardId), ct)).SpaceId, q, ct))
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(503);
        app.MapGet("/boards/{boardId:guid}/youtrack/resolve", async (Guid boardId, string reference, IMessageBus bus, YouTrackReadRunner runner, CancellationToken ct)
                => await YouTrackEndpoints.ResolveAsync(runner, (await bus.InvokeAsync<BoardSpace>(new GetBoardSpace(boardId), ct)).SpaceId, reference, ct))
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(503);
        // A paste of up to 100 references: one request, one board decision and one YouTrack search instead of one of each per issue.
        app.MapPost("/boards/{boardId:guid}/youtrack/resolve", async (Guid boardId, YouTrackResolveRequest request, IMessageBus bus, YouTrackReadRunner runner, CancellationToken ct)
                => await YouTrackEndpoints.ResolveManyAsync(runner, (await bus.InvokeAsync<BoardSpace>(new GetBoardSpace(boardId), ct)).SpaceId, request, ct))
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(503);
    }
}
