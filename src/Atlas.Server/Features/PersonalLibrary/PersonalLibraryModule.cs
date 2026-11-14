using Atlas.Server.Features.PersonalLibrary.Domain;

namespace Atlas.Server.Features.PersonalLibrary;

public static class PersonalLibraryModule
{
    public static IServiceCollection AddPersonalLibrary(this IServiceCollection services)
    {
        services.AddScoped<PersonalPageGuard>();
        services.AddMcpServer().WithTools<RecentChangesMcpTools>();
        return services;
    }
}
