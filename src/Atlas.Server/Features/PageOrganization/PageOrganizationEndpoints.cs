using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.PersonalApi;
using Wolverine;

namespace Atlas.Server.Features.PageOrganization;

public static class PageOrganizationEndpoints
{
    public static void MapPageOrganization(this WebApplication app)
    {
        MapRoutes(app.MapGroup("/api").RequireAuthorization(), false);
        MapRoutes(app.MapGroup("/api/v1").WithGroupName("v1")
            .RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void MapRoutes(RouteGroupBuilder group, bool personalApi)
    {
        var tree = group.MapGet("/spaces/{spaceId:guid}/tree", async (Guid spaceId, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PageTreeDto>(new GetPageTree(spaceId), ct)));
        var trashList = group.MapGet("/spaces/{spaceId:guid}/trash", async (Guid spaceId, int? offset, int? limit, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PageTrashDto>(new ListPageTrash(spaceId, offset ?? 0, limit ?? 50), ct)));
        var move = group.MapPost("/pages/{id:guid}/move", async (Guid id, MovePageRequest request, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<OrganizationChange>(new MovePage(id, request), ct)));
        var sort = group.MapPost("/spaces/{spaceId:guid}/tree/sort", async (Guid spaceId, SortTreeLevelRequest request, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<OrganizationChange>(new SortTreeLevel(spaceId, request), ct)));
        sort.Produces<OrganizationChange>().ProducesProblem(403)
            .WithSummary("Řadit abecedně: pin the direct children of parentId (null: the space root) to their titles, or unpin them. Writer on the space for the root, edit rights on the parent page otherwise. A hand move within a pinned level unpins it (titleOrderCancelled).");
        var moveSpace = group.MapPost("/pages/{id:guid}/move-space", async (Guid id, MovePageSpaceRequest request, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<CrossSpaceChange>(new MovePageToSpace(id, request), ct)));
        moveSpace.Produces<CrossSpaceChange>().ProducesProblem(409)
            .WithSummary("Move a page/subtree between spaces. Requires Writer in both, both tree versions, page version and explicit access-change acknowledgement. Save live edits first.");
        var trash = group.MapPost("/pages/{id:guid}/trash", async (Guid id, TrashPageRequest request, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<OrganizationChange>(new TrashPage(id, request), ct)));
        var untrash = group.MapPost("/pages/{id:guid}/untrash", async (Guid id, UntrashPageRequest request, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<OrganizationChange>(new UntrashPage(id, request), ct)));
        var rename = group.MapPut("/pages/{id:guid}/title", async (Guid id, RenamePageRequest request, IMessageBus bus, CancellationToken ct) =>
        {
            // Two transactions, exactly as before: the rename commits, then the page is read back.
            await bus.InvokeAsync(new RenamePage(id, request), ct);
            return Results.Ok(await bus.InvokeAsync<PageDto>(new GetPage(id), ct));
        });
        tree.Produces<PageTreeDto>().ProducesProblem(404).WithSummary("Get the ordered accessible page tree and its concurrency version.");
        trashList.Produces<PageTrashDto>().ProducesProblem(404).WithSummary("List recoverable pages. Requires Admin role in the space; offset/limit pagination.");
        move.Produces<OrganizationChange>().ProducesProblem(409).WithSummary("Move a page within its space using expectedTreeVersion. Cycles and hidden parents are rejected.");
        trash.Produces<OrganizationChange>().ProducesProblem(409).WithSummary("Move a page to trash. Descendants require explicit includeSubtree; expectedVersion and expectedTreeVersion protect edits. Unsaved live edits by other users in the selection return 409 collaboration_unsaved.");
        untrash.Produces<OrganizationChange>().ProducesProblem(409).WithSummary("Restore a trashed page or its deletion batch. Requires space Admin; content and publication are preserved.");
        rename.Produces<PageDto>().ProducesProblem(409).WithSummary("Create a title-only working revision using expectedVersion. Publish separately to update the public title.");
        if (personalApi)
        {
            tree.WithName("GetSpaceTree");
            trashList.WithName("ListSpaceTrash");
            moveSpace.WithName("MovePageToSpace").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageMoveSpace);
            move.WithName("MovePage");
            sort.WithName("SortTreeLevel").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.TreeSort);
            trash.WithName("TrashPage");
            untrash.WithName("RestoreTrashedPage");
            rename.WithName("RenamePage");
            // Moves and the trash change at once what readers see (plan D2); a rename is a working revision.
            move.RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageMove);
            trash.RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageTrash);
            untrash.RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageUntrash);
            rename.RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.PageRename);
        }
    }
}
