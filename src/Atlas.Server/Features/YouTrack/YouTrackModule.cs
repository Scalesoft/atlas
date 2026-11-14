using Atlas.Server.Features.Access.Domain;

namespace Atlas.Server.Features.YouTrack;

public static class YouTrackModule
{
    public static IServiceCollection AddYouTrack(this IServiceCollection services)
    {
        services.AddSingleton<YouTrackReadCache>();
        services.AddScoped<YouTrackActor>();
        services.AddScoped<YouTrackSpaceAccess>();
        services.AddScoped<YouTrackConfigurationReader>();
        services.AddScoped<YouTrackReadRunner>();
        services.AddScoped<IAccessKind, YouTrackAccessKind>();
        return services;
    }
}
