using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atlas.Server.Features.ContentExport;

public static class ContentExportModule
{
    public static IServiceCollection AddContentExport(this IServiceCollection services)
    {
        services.AddSingleton<ContentExportFiles>();
        services.AddSingleton<ContentPdfRenderer>();
        // The download endpoint stamps its final authorization with the clock the transfer measures it by.
        services.TryAddSingleton(TimeProvider.System);
        services.AddRateLimiter(options => options.AddConcurrencyLimiter("content-export", limit =>
        {
            limit.PermitLimit = 2;
            limit.QueueLimit = 0;
        }));
        return services;
    }
}
