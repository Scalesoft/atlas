using Atlas.Server.Features.Spaces.Commands;
using Atlas.Server.Features.Spaces.Contracts;
using Atlas.Server.Features.Spaces.Queries;
using Wolverine;

namespace Atlas.Server.Features.Spaces;

public static class SpacesEndpoints
{
    public static void MapSpaces(this IEndpointRouteBuilder app)
    {
        var spaces = app.MapGroup("/api/spaces").RequireAuthorization();
        spaces.MapGet("", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok((await bus.InvokeAsync<AccessibleSpaces>(new GetAccessibleSpaces(), ct)).Items))
            .Produces<IReadOnlyList<SpaceListItem>>();
        spaces.MapGet("/{spaceId:guid}/pages", async (Guid spaceId, IMessageBus bus, CancellationToken ct)
                => Results.Ok((await bus.InvokeAsync<SpacePages>(new GetSpacePages(spaceId), ct)).Items))
            .Produces<IReadOnlyList<SpacePageListItem>>()
            .ProducesProblem(404);
        // The space trash is the wiki admin's (unified trash spec §3); MCP and the personal API do not offer it.
        spaces.MapPost("/{spaceId:guid}/trash", async (Guid spaceId, TrashSpaceRequest request, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new TrashSpace(spaceId, request), ct);
                return Results.NoContent();
            })
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        spaces.MapPost("/{spaceId:guid}/restore", async (Guid spaceId, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new RestoreSpace(spaceId), ct);
                return Results.NoContent();
            })
            .ProducesProblem(403).ProducesProblem(404);
        // The permanent delete (unified trash spec §3): the wiki admin types the name; web only, as the space trash (rule 6).
        spaces.MapPost("/{spaceId:guid}/purge", async (Guid spaceId, PurgeSpaceRequest request, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new PurgeSpace(spaceId, request), ct);
                return Results.NoContent();
            })
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404);
        app.MapGet("/api/space-trash", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<TrashedSpaceItem>>(new ListSpaceTrash(), ct)))
            .RequireAuthorization()
            .Produces<IReadOnlyList<TrashedSpaceItem>>()
            .ProducesProblem(403);
        app.MapPut("/api/me/spaces/{spaceId:guid}/pin", async (Guid spaceId, SpacePinState request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SpacePinState>(new SetSpacePin(spaceId, request.Pinned), ct)))
            .RequireAuthorization()
            .Produces<SpacePinState>()
            .ProducesProblem(404)
            .ProducesProblem(409);
    }
}
