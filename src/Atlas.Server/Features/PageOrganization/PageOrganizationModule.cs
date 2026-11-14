using Atlas.Server.Features.PageOrganization.Domain;

namespace Atlas.Server.Features.PageOrganization;

public static class PageOrganizationModule
{
    public static IServiceCollection AddPageOrganization(this IServiceCollection services)
    {
        services.AddScoped<OrganizationLocks>();
        // The page's trash on the object path (unified trash spec rule 5).
        services.AddScoped<Commands.UntrashPageHandler>();
        services.AddScoped<Platform.Objects.IObjectTrash, Commands.PageObjectTrash>();
        return services;
    }
}
