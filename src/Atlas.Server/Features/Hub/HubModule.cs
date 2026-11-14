using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atlas.Server.Features.Hub;

public static class HubModule
{
    public static IServiceCollection AddHub(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDataProtection();
        services.AddHttpClient("hub-directory", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton<HubSynchronizer>();
        services.AddSingleton<HubAdministrationService>();
        services.AddSingleton<Migration.HubMigrationService>();
        services.AddSingleton(new Atlas.Server.Features.ModuleSettings.Contracts.ModuleSettingsDescriptor(
            "hub", "Skupiny Hubu", "Ověřování členství a oprávnění podle skupin JetBrains Hubu.", "hub", 30, false));
        return services;
    }
}
