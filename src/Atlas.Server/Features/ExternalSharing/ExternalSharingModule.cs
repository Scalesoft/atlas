using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Administration;

namespace Atlas.Server.Features.ExternalSharing;

public static class ExternalSharingModule
{
    public static IServiceCollection AddExternalSharing(this IServiceCollection services)
    {
        services.TryAddScoped<AdministrationAccess>();
        services.AddScoped<ExternalSharingAccess>();
        services.AddScoped<ShareLinkReader>();
        // The download endpoint stamps its authorization with the clock the transfer measures it by.
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
