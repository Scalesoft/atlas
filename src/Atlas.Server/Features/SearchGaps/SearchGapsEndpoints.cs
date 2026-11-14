using Atlas.Server.Features.SearchGaps.Commands;
using Atlas.Server.Features.SearchGaps.Contracts;
using Atlas.Server.Features.SearchGaps.Queries;
using Wolverine;

namespace Atlas.Server.Features.SearchGaps;

public static class SearchGapsEndpoints
{
    public static void MapSearchGaps(this IEndpointRouteBuilder app)
    {
        var gaps = app.MapGroup("/api/spaces/{spaceId:guid}/search-gaps").RequireAuthorization();
        gaps.MapGet("", async (Guid spaceId, int? days, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SearchGapList>(new GetSearchGaps(spaceId, days ?? 30), ct)))
            .Produces<SearchGapList>()
            .ProducesProblem(404);
        gaps.MapPost("/resolutions", async (Guid spaceId, ResolveSearchGapRequest request, IMessageBus bus, CancellationToken ct) =>
            {
                await bus.InvokeAsync(new ResolveSearchGap(spaceId, request.NormalizedQuery ?? ""), ct);
                return Results.NoContent();
            })
            .Produces(204)
            .ProducesProblem(404);
    }
}
