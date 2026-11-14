using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.SearchGaps;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Search;

public static class SearchModule
{
    public static IServiceCollection AddSearch(this IServiceCollection services)
    {
        services.AddScoped<SearchSql>();
        services.AddJob<SearchDocumentsRebuildJob>();
        // G2: the miss recorder is in-memory and shared by every request; hosts without the flusher (tests) still search.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SearchMissRecorder>();
        services.AddSingleton<ISearchMissRecorder>(provider => provider.GetRequiredService<SearchMissRecorder>());
        // S1/S2: the query vector of the endpoints. It reads the AI configuration from memory (AddAi) and calls no model
        // without the semantic-search capability.
        services.TryAddSingleton<QueryEmbeddingCache>();
        services.TryAddScoped<ISemanticQuery, SemanticQueryService>();
        return services;
    }
}
