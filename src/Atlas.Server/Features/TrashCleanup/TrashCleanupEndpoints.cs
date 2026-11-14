using Atlas.Server.Features.TrashCleanup.Commands;
using Atlas.Server.Features.TrashCleanup.Contracts;
using Wolverine;

namespace Atlas.Server.Features.TrashCleanup;

/// <summary>Cleanup of orphaned items (unified trash spec §4b): the wiki's rule (a wiki admin) and each space's with „Vyčistit teď“
/// (a space admin; a member gets 403, anyone else the 404 of a space it cannot see). Administrative, so the web's only: no MCP (rule 6).</summary>
public static class TrashCleanupEndpoints
{
    public static void MapTrashCleanup(this IEndpointRouteBuilder app)
    {
        var wiki = app.MapGroup("/api/admin/trash-cleanup").RequireAuthorization();
        wiki.MapGet("", async (IMessageBus bus, CancellationToken ct) => Results.Ok(await bus.InvokeAsync<WikiTrashCleanupView>(new GetWikiTrashCleanup(), ct)))
            .Produces<WikiTrashCleanupView>()
            .ProducesProblem(403);
        wiki.MapPut("", async (SaveWikiTrashCleanupRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<WikiTrashCleanupView>(new SaveWikiTrashCleanup(request), ct)))
            .Produces<WikiTrashCleanupView>()
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(409);

        var space = app.MapGroup("/api/spaces/{spaceId:guid}/trash-cleanup").RequireAuthorization();
        space.MapGet("", async (Guid spaceId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SpaceTrashCleanupView>(new GetSpaceTrashCleanup(spaceId), ct)))
            .Produces<SpaceTrashCleanupView>()
            .ProducesProblem(404);
        space.MapPut("", async (Guid spaceId, SaveSpaceTrashCleanupRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SpaceTrashCleanupView>(new SaveSpaceTrashCleanup(spaceId, request), ct)))
            .Produces<SpaceTrashCleanupView>()
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
        // „Vyčistit teď“: the rule in effect, as the signed-in admin; authorized by reading it first, each item its own transaction.
        space.MapPost("/run", async (Guid spaceId, IMessageBus bus, TrashCleanupRunner runner, CancellationToken ct) =>
            {
                var rule = await bus.InvokeAsync<SpaceTrashCleanupView>(new GetSpaceTrashCleanup(spaceId), ct);
                var result = await runner.RunAsync(spaceId, rule.EffectiveDays, DateTime.UtcNow, ct);
                return Results.Ok(result);
            })
            .Produces<TrashCleanupResult>()
            .ProducesProblem(404);
    }
}
