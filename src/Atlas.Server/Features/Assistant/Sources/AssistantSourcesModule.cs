using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Assistant.Sources;

namespace Atlas.Server.Features.Assistant;

public static class AssistantSourcesModule
{
    /// <summary>Q2 (AI-11): the source adapters of answers and their registry. In production the page adapter only; a test host
    /// adds its synthetic second type next to it. Needs AddSearch (SearchSql) and the platform's content access.</summary>
    public static IServiceCollection AddAssistantSources(this IServiceCollection services)
    {
        services.AddScoped<AnswerSourceSql>();
        services.AddScoped<IKnowledgeSourceAdapter, PageSourceAdapter>();
        services.AddScoped<SourceRegistry>();
        // The optional rerank step of answer sources (search.rerank); without the rerank profile today's sources.
        services.TryAddSingleton<IAnswerReranker, ProfileAnswerReranker>();
        services.TryAddSingleton<AnswerRerank>();
        return services;
    }
}
