using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.AiIndex;

/// <summary>
/// The knowledge index of published content (Atlas 2.0 V2): generations, pages, chunks and the task queue. Without
/// a generation nothing is enqueued and the worker reads only the master switch, so a wiki without AI pays nothing (AI-24).
/// </summary>
public static class AiIndexModule
{
    public static IServiceCollection AddAiIndex(this IServiceCollection services)
    {
        services.AddScoped<IndexStore>();
        services.AddScoped<IndexViews>();
        services.AddScoped<IndexVerificationRunner>();
        // The hooks of the AI settings (AiSettingsHooks): the configuration, the overview tile and the profile save ask the index.
        services.AddScoped<IActiveGenerationReader, ActiveGenerationReader>();
        services.AddScoped<IAiIndexOverview, AiIndexOverview>();
        services.AddScoped<IEmbeddingChangeGuard, ActiveIndexGenerationGuard>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IndexRunner>();
        services.TryAddSingleton<IndexMaintenanceRunner>();
        services.AddJob<IndexMaintenance>();
        return services;
    }

    /// <summary>The index job. Test hosts leave it out and call <see cref="IndexRunner.RunOnceAsync"/>; the maintenance is a job
    /// (<see cref="IndexMaintenance"/>), whose runner tests call through <see cref="IndexMaintenanceRunner.RunOnceAsync"/>.</summary>
    public static IServiceCollection AddAiIndexBackgroundServices(this IServiceCollection services)
    {
        services.AddJob<IndexJob>();
        return services;
    }
}
