using Wolverine;
using Atlas.Server.Features.PageAccess.Commands;
using Atlas.Server.Features.PageAccess.Queries;

namespace Atlas.Server.Features.PageAccess;

public static class PageAccessEndpoints
{
    public static void MapPageAccess(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/pages/{pageId:guid}/access-history", async (Guid pageId, long? beforeId, int? limit,
            IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PageAccessHistory>(new GetPageAccessHistory(pageId, beforeId, limit ?? 50), ct)))
            .RequireAuthorization();
        app.MapGet("/api/pages/{pageId:guid}/access", async (Guid pageId, int? offset, int? limit,
            string? q, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PageAccessState>(new GetPageAccess(pageId, offset ?? 0, limit ?? 50, q), ct)))
            .RequireAuthorization();
        app.MapPut("/api/pages/{pageId:guid}/access/mode", async (Guid pageId, UpdatePageAccessMode request, IMessageBus bus,
            CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PageAccessModeResult>(new SetPageAccessMode(pageId, request), ct)))
            .RequireAuthorization();
        app.MapPut("/api/pages/{pageId:guid}/access/grants", async (Guid pageId, UpdatePageGrant request, IMessageBus bus,
            CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetPageGrant(pageId, request), ct);
            return Results.NoContent();
        }).RequireAuthorization();
        app.MapGet("/api/pages/{pageId:guid}/access/explain", async (Guid pageId, Guid userId, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PageAccessExplanation>(new ExplainPageAccess(pageId, userId), ct)))
            .RequireAuthorization();
        app.MapGet("/api/pages/{pageId:guid}/access/candidates", async (Guid pageId, string? kind, string? q, IMessageBus bus,
            CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<IReadOnlyList<AccessCandidate>>(new GetPageAccessCandidates(pageId, kind ?? "user", q), ct)))
            .RequireAuthorization();
        app.MapGet("/api/pages/{pageId:guid}/people", async (Guid pageId, string? q, int? limit, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<IReadOnlyList<PagePerson>>(new SearchPagePeople(pageId, q, limit), ct)))
            .RequireAuthorization();
        app.MapGet("/api/pages/{pageId:guid}/people/{userId:guid}", async (Guid pageId, Guid userId, IMessageBus bus,
            CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<PagePerson>(new GetPagePerson(pageId, userId), ct)))
            .RequireAuthorization();
    }
}
