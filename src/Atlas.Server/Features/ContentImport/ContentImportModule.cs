using Atlas.Server.Features.ContentImport.Domain;

namespace Atlas.Server.Features.ContentImport;

public static class ContentImportModule
{
    public static IServiceCollection AddContentImport(this IServiceCollection services)
    {
        services.AddSingleton<ContentImportFiles>();
        services.AddSingleton<ImportWorkGate>();
        return services;
    }
}
