using Atlas.Server.Features.PageHistory.Domain;

namespace Atlas.Server.Features.PageHistory;

public static class PageHistoryModule
{
    public static IServiceCollection AddPageHistory(this IServiceCollection services)
    {
        services.AddScoped<PageHistoryAccess>();
        return services;
    }
}
