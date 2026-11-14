using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.Administration.Commands;

namespace Atlas.Server.Features.Administration;

public static class AdministrationModule
{
    public static IServiceCollection AddAdministration(this IServiceCollection services)
    {
        services.AddScoped<AdministrationAccess>();
        // The space access kind writes through the handler of PUT /api/spaces/{id}/access.
        services.AddScoped<SetSpaceGrantHandler>();
        services.AddScoped<IAccessKind, WikiAccessKind>();
        services.AddScoped<IAccessKind, SpaceAccessKind>();
        return services;
    }
}
