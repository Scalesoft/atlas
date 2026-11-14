using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.TreeReferences.Commands;
using Atlas.Server.Features.TreeReferences.Contracts;
using Wolverine;

namespace Atlas.Server.Features.TreeReferences;

/// <summary>Reference nodes of the space tree for the web and /api/v1 (spec 2026-10-27 §13.3); the tree lists them (GET …/tree).</summary>
public static class TreeReferenceEndpoints
{
    public static void MapTreeReferences(this WebApplication app)
    {
        MapRoutes(app.MapGroup("/api").RequireAuthorization(), false);
        MapRoutes(app.MapGroup("/api/v1").WithGroupName("v1")
            .RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void MapRoutes(RouteGroupBuilder group, bool personalApi)
    {
        var add = group.MapPost("/spaces/{spaceId:guid}/tree/references", async (Guid spaceId, AddTreeReferenceRequest request, IMessageBus bus, CancellationToken ct) =>
            {
                var node = await bus.InvokeAsync<PageTreeItem>(new AddTreeReference(spaceId, request), ct);
                return Results.Created($"/api/tree/references/{node.Id}", node);
            })
            .Produces<PageTreeItem>(201).ProducesProblem(400).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Place a reference node to any readable object (page, section, board, contact) under parentId or at the root, before beforeId. Requires Writer in the space.");
        var remove = group.MapDelete("/tree/references/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new RemoveTreeReference(id), ct);
                return Results.NoContent();
            })
            .Produces(204).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Remove a reference node from the tree; only the link goes, the target stays. Requires Writer in the space.");
        var move = group.MapPost("/tree/references/{id:guid}/move", async (Guid id, MovePageRequest request, IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.InvokeAsync<OrganizationChange>(new MoveTreeReference(id, request), ct)))
            .Produces<OrganizationChange>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Move a reference node within its space using expectedTreeVersion, as a page move.");
        if (personalApi)
        {
            // A node changes at once what readers of the tree see (plan D2), as a move does.
            add.WithName("AddTreeReference").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.TreeReferenceAdd);
            remove.WithName("RemoveTreeReference").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.TreeReferenceRemove);
            move.WithName("MoveTreeReference").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.TreeReferenceMove);
        }
    }
}
