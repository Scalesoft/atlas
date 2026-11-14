using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.YouTrack;

public static class YouTrackModule
{
    public static IServiceCollection AddAtlasYouTrack(this IServiceCollection services, IConfiguration configuration)
    {
        // An installed but unconfigured integration must not prevent first-run onboarding.
        // Credentials and per-space project allowlists can be configured after first admin creation.
        services.AddOptions<YouTrackOptions>().Configure(options => options.Enabled = true).Bind(configuration.GetSection("YouTrack"));
        services.AddHttpClient<YouTrackClient>(client => client.Timeout = TimeSpan.FromSeconds(8))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        return services;
    }
}
