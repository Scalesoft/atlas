using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Pipeline;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Usage;
using Atlas.Server.Features.Ai.Settings.SavedProfiles;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Features.ModuleSettings.Contracts;

namespace Atlas.Server.Features.Ai;

/// <summary>
/// The optional AI module (Atlas 2.0, AI-24): without a saved master switch and an enabled profile nothing in the wiki
/// uses a model. A1 registers the configuration, A2 the clients, their pipeline and usage, A3 the configuration cache, the
/// circuit breaker with its probes and the capabilities; the index registers in its own module (V2).
/// </summary>
public static class AiModule
{
    public static IServiceCollection AddAi(this IServiceCollection services)
    {
        // API keys are protected with the key ring the auth cookies already persist (AuthCookieModule).
        services.AddDataProtection();
        services.TryAddScoped<ModuleSettingsAccess>();
        services.AddScoped<AiSettingsWrite>();
        services.AddScoped<SavedProfileStore>();
        services.AddScoped<SavedProfileConnectionRunner>();
        services.TryAddSingleton(TimeProvider.System);
        // Availability (A3): the in-process configuration and the circuit breaker, which hears every call as the outcome sink.
        // Pushed, not polled: the capabilities feed for every account and the health feed for the wiki administrators (/hubs/live).
        services.TryAddSingleton<AiLiveSignals>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Atlas.Server.Platform.Live.ILiveChannel, AiLiveChannel>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Atlas.Server.Platform.Live.ILiveChannel, AiAdminLiveChannel>());
        services.TryAddSingleton<AiAvailability>();
        services.TryAddSingleton<IAiOutcomeSink>(provider => provider.GetRequiredService<AiAvailability>());
        services.TryAddSingleton<IAiConfigurationLoader, WolverineAiConfigurationLoader>();
        services.TryAddSingleton<AiConfigurationCache>();
        services.TryAddSingleton<IAiConfigurationChanged>(provider => provider.GetRequiredService<AiConfigurationCache>());
        services.TryAddSingleton<AiProbeRunner>();
        services.TryAddSingleton<AiCapabilities>();
        services.TryAddScoped<AiGate>();
        services.AddScoped<Access.AiUserAccess>();
        services.AddScoped<Access.AiPolicyResolver>();
        // Clients (A2). The test hosts replace IAiClientFactory with the fake provider.
        services.AddAiHttp();
        services.TryAddSingleton<AiUsageRecorder>();
        services.TryAddSingleton<AiConcurrencyGate>();
        services.TryAddSingleton<AiPipeline>();
        services.TryAddSingleton<IAiClientFactory, AiClientFactory>();
        services.AddScoped<TestAiProfileRunner>();
        services.AddScoped<DiscoverAiModelsRunner>();
        services.AddSingleton(new ModuleSettingsDescriptor("ai", "AI",
            "Významové hledání, související stránky a návrhy nad obsahem wiki. Volitelné; bez nastavení se wiki chová jako dosud.",
            "ai", 21, false));
        return services;
    }

    /// <summary>The usage flusher and the availability probes. Tests leave them out and call
    /// <see cref="AiUsageFlusher.FlushAsync"/>, the commands or <see cref="AiProbeRunner.RunOnceAsync"/>.</summary>
    public static IServiceCollection AddAiBackgroundServices(this IServiceCollection services)
    {
        services.AddSingleton<AiUsageFlusher>();
        services.AddHostedService(provider => provider.GetRequiredService<AiUsageFlusher>());
        services.AddHostedService<AiProbeService>();
        return services;
    }
}
