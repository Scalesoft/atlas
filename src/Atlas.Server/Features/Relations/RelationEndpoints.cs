using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Queries;
using Atlas.Server.Features.Relations.Persistence;
using Atlas.Server.Features.Relations.Domain;
using Wolverine;

namespace Atlas.Server.Features.Relations;

/// <summary>The space's contents and an object's relations (spec 2026-10-27 §8, §13.3), for the web and the personal API alike.</summary>
public static class RelationEndpoints
{
    public static IServiceCollection AddRelations(this IServiceCollection services) =>
        services.AddScoped<RelationNodes>().AddScoped<RelationReader>().AddScoped<ObjectUsage>().AddScoped<UsageReasons>();

    public static void MapRelations(this WebApplication app)
    {
        MapRoutes(app.MapGroup("/api").RequireAuthorization(), false);
        MapRoutes(app.MapGroup("/api/v1").WithGroupName("v1")
            .RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void MapRoutes(RouteGroupBuilder group, bool personalApi)
    {
        var contents = group.MapGet("/spaces/{spaceId:guid}/contents", async (Guid spaceId, string? type, string? relation, string? q, string? sort, string? cursor,
                IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<SpaceContents>(new ListSpaceContents(spaceId, type, relation, q, sort, cursor), ct)));
        var relations = group.MapGet("/objects/{objectId:guid}/relations", async (Guid objectId, int? depth, string? kinds, bool? drafts, bool? reasons,
                IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<ObjectRelations>(new GetObjectRelations(objectId, depth ?? 1, kinds, drafts ?? false, reasons ?? false), ct)));
        var usage = group.MapGet("/usage/{objectId:guid}", async (Guid objectId, bool? drafts, string? cursor, string? part, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<ObjectUsageList>(new GetObjectUsage(objectId, drafts ?? true, cursor, part), ct)));
        var count = group.MapGet("/usage/{objectId:guid}/count", async (Guid objectId, bool? drafts, string? part, bool? fresh, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<UsageCount>(new CountObjectUsage(objectId, drafts ?? true, part, fresh ?? false), ct)));
        var inventory = group.MapGet("/spaces/{spaceId:guid}/usage", async (Guid spaceId, string? type, bool? unused, string? cursor, IMessageBus bus,
                CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<SpaceUsage>(new ListSpaceUsage(spaceId, type, unused ?? false, cursor), ct)));
        contents.Produces<SpaceContents>().ProducesProblem(400).ProducesProblem(404);
        relations.Produces<ObjectRelations>().ProducesProblem(400).ProducesProblem(404);
        usage.Produces<ObjectUsageList>().ProducesProblem(400).ProducesProblem(404);
        count.Produces<UsageCount>().ProducesProblem(404);
        inventory.Produces<SpaceUsage>().ProducesProblem(400).ProducesProblem(404);
        if (!personalApi) return;
        usage.WithName("GetObjectUsage")
            .WithSummary("Where an object is used (Použito na): the pages, boards and tree places the token's owner may read, each with the relation kind, the card, " +
                "reference node or block and a short excerpt, and whether only a working draft holds it; total and hidden count every use. drafts (default true), cursor; part keeps the uses of one part (a catalog requirement's key on its catalog page).");
        count.WithName("CountObjectUsage")
            .WithSummary("How many places use an object: total, visible to the token's owner and hidden. drafts (default true).");
        inventory.WithName("ListSpaceUsage")
            .WithSummary("The components of a space by type with their uses (visible and hidden) and whether each is unused; type, unused, cursor.");
        contents.WithName("ListSpaceContents")
            .WithSummary("The pages, boards and objects of a space the token's owner may read, and the wiki's objects used in it, each with uses, links in and out " +
                "and comments; summary counts by type and relation over the whole space. Filters type, relation (unused, noIncoming, broken, attached, mostUsed), " +
                "q (title words); sort title, updated or used; page with nextCursor.");
        relations.WithName("GetObjectRelations")
            .WithSummary("The relations of an object (link, placement, attached, mention, reference) as nodes and edges, depth 1 or 2, optionally only some kinds; " +
                "drafts adds uses only a working draft holds, reasons the card, node or block and an excerpt of each use. An end the token's owner may not read is a " +
                "node with readable false and nothing else; hidden counts the object's edges to such ends.");
    }
}
