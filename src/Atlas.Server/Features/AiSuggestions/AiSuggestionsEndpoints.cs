using Atlas.Server.Features.AiSuggestions.Contracts;

namespace Atlas.Server.Features.AiSuggestions;

/// <summary>E2, E4, P2 and P3 for signed-in users (rate limit <c>ai-assist</c>). P1 extends <c>GET /api/search/similar-pages</c>.</summary>
public static class AiSuggestionsEndpoints
{
    public static void MapAiSuggestions(this IEndpointRouteBuilder app)
    {
        var assist = app.MapGroup("/api/assist").RequireAuthorization().RequireRateLimiting("ai-assist");
        assist.MapPost("/link-candidates", async (LinkCandidatesRequest request, AiSuggestionsFlow flow, CancellationToken ct)
                => Results.Ok(await flow.LinkCandidatesAsync(request, ct)))
            .Produces<LinkCandidatesResponse>()
            .ProducesProblem(400)
            .ProducesProblem(404);
        assist.MapPost("/page-order", async (PageOrderRequest request, AiSuggestionsFlow flow, CancellationToken ct)
                => Results.Ok(await flow.PageOrderAsync(request, ct)))
            .Produces<PageOrderResponse>()
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(503);

        var pages = app.MapGroup("/api/pages").RequireAuthorization().RequireRateLimiting("ai-assist");
        pages.MapGet("/placement-suggestion", async (string? title, Guid spaceId, AiSuggestionsFlow flow, CancellationToken ct)
                => await flow.PlacementAsync(title, spaceId, ct) is { } suggestion ? Results.Ok(suggestion) : Results.NoContent())
            .Produces<PlacementSuggestion>()
            .Produces(204)
            .ProducesProblem(400)
            .ProducesProblem(503);
        pages.MapGet("/template-suggestion", async (string? title, Guid spaceId, AiSuggestionsFlow flow, CancellationToken ct)
                => await flow.TemplateAsync(title, spaceId, ct) is { } suggestion ? Results.Ok(suggestion) : Results.NoContent())
            .Produces<TemplateSuggestion>()
            .Produces(204)
            .ProducesProblem(400);
    }
}
