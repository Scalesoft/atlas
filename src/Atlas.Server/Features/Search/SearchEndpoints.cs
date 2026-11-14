using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.AiSuggestions;
using Atlas.Server.Features.AiSuggestions.Queries;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.Search.Queries;
using Wolverine;

namespace Atlas.Server.Features.Search;

public static class SearchEndpoints
{
    public static void MapSearch(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/search", async (string? q, Guid? spaceId, int? offset, int? limit, string? content,
                string? changedWithin, string? sort, bool? fuzzy, bool? recordMiss, Guid? contextSpaceId, bool? relaxed, ISemanticQuery semantic,
                IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.SearchAsync(semantic, new Queries.Search(q, spaceId, offset ?? 0, limit ?? 20,
                    new SearchOptions(content ?? "all", changedWithin ?? "any", sort ?? "relevance"))
                    { Fuzzy = fuzzy ?? true, RecordMiss = recordMiss ?? false, ContextSpaceId = contextSpaceId, Relaxed = relaxed }, ct)))
            .RequireAuthorization()
            .RequireRateLimiting("interactive-search");
        app.MapGet("/api/search/similar-pages", async (string? title, Guid? spaceId, Guid? excludePageId, int? limit,
                WorkingTextEmbedder embedder, IMessageBus bus, CancellationToken ct) =>
                Results.Ok(await bus.FindSimilarPagesAsync(embedder,
                    new FindSimilarPages(title, spaceId, excludePageId, limit ?? FindSimilarPages.DefaultLimit), ct)))
            .RequireAuthorization()
            .RequireRateLimiting("interactive-search");
    }

    /// <summary>
    /// Every search route (<c>/api/search</c> and the Personal API's <c>/api/v1/search</c>): the query vector first, outside any
    /// transaction and lock and within its 300 ms budget (plan §1.1, §11), then the handler. Without a vector the search is
    /// today's lexical one, byte for byte (AI-24).
    /// </summary>
    public static async Task<SearchResponse> SearchAsync(this IMessageBus bus, ISemanticQuery semantic, Queries.Search search, CancellationToken ct)
    {
        var vector = await semantic.TryEmbedAsync(search.Query, search.Options, search.Fuzzy, search.Offset, ct);
        return await bus.InvokeAsync<SearchResponse>(vector is null ? search : search with { Semantic = vector }, ct);
    }

    /// <summary>
    /// P1 (V5): the title is the author's working text, embedded before the handler (outside any transaction, 300 ms budget)
    /// only with the <c>createSuggestions</c> capability (memory; it includes the cloud permission) and, when the creation's
    /// space is known, only when that space is included in AI. Without a vector the suggestions are today's, byte for byte.
    /// </summary>
    public static async Task<SimilarPagesResponse> FindSimilarPagesAsync(this IMessageBus bus, WorkingTextEmbedder embedder, FindSimilarPages query,
        CancellationToken ct)
    {
        if (query.Limit is >= 1 and <= 5 && query.Title is { Length: <= 200 } title && SearchText.Normalize(title).Length >= 3
            && await embedder.AvailableAsync(AiTaskCatalog.PageCreateSuggest, ct)
            && (query.SpaceId is not { } spaceId || (await bus.InvokeAsync<CreationScope>(new GetCreationScope(spaceId), ct)).InAi)
            && await embedder.EmbedAsync(AiTaskCatalog.PageCreateSuggest, [title.Trim()], ct) is { } vectors && vectors.Vectors[0] is { } vector)
        {
            query = query with { Semantic = new SemanticQuery(vectors.GenerationId, vector) };
        }
        return await bus.InvokeAsync<SimilarPagesResponse>(query, ct);
    }
}
