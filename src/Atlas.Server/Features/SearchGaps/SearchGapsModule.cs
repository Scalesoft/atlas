namespace Atlas.Server.Features.SearchGaps;

/// <summary>G2 "Nenalezené dotazy". The in-memory recorder is registered by AddSearch, so every host that searches has it.</summary>
public static class SearchGapsModule
{
    public static IServiceCollection AddSearchGaps(this IServiceCollection services) => services.AddScoped<SearchGapStore>().AddScoped<SearchMissWriter>();

    /// <summary>The flush and purge loop. Tests leave it out and call SearchMissFlusher.FlushAsync or the commands directly.</summary>
    public static IServiceCollection AddSearchGapBackgroundServices(this IServiceCollection services)
    {
        services.AddSingleton<SearchMissFlusher>();
        services.AddHostedService(provider => provider.GetRequiredService<SearchMissFlusher>());
        return services;
    }
}
