using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.AiSuggestions;

/// <summary>E2, E4 and the vector part of P1–P3 (plan phase 2 §13). Reads the AI configuration from memory (AddAi) and calls no
/// model without the capability of its task.</summary>
public static class AiSuggestionsModule
{
    public static IServiceCollection AddAiSuggestions(this IServiceCollection services)
    {
        services.AddScoped<SemanticSuggestionSql>();
        services.AddScoped<AssistPageGuard>();
        services.AddScoped<CreationScopeReader>();
        services.AddScoped<AiSuggestionsFlow>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<QueryEmbeddingCache>();
        services.TryAddScoped<WorkingTextEmbedder>();
        return services;
    }
}
