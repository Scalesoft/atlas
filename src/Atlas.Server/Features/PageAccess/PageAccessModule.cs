using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.PageAccess.Commands;

namespace Atlas.Server.Features.PageAccess;

public static class PageAccessModule
{
    public static IServiceCollection AddPageAccess(this IServiceCollection services)
    {
        // The page access kind writes through the handler of PUT /api/pages/{id}/access/grants.
        services.AddScoped<SetPageGrantHandler>();
        services.AddScoped<IAccessKind, PageAccessKind>();
        return services;
    }
}
