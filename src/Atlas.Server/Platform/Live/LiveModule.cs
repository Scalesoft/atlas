using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atlas.Server.Platform.Live;

public static class LiveModule
{
    /// <summary>The platform half of /hubs/live (channels, view sessions, the object channel); modules add their channel types as
    /// <see cref="ILiveChannel"/>.</summary>
    public static IServiceCollection AddLive(this IServiceCollection services)
    {
        // Channel names and presence (at most 50 dragged items) are small; the collaboration hub's 8 MiB is for Yjs state.
        // Every hub (also the collaboration one): a call cancelled by its closed connection logs quietly.
        services.AddSingleton<CancelledHubCalls>();
        services.AddSignalR(options => options.AddFilter<CancelledHubCalls>()).AddHubOptions<LiveHub>(options => options.MaximumReceiveMessageSize = 64 * 1024);
        // SignalR logs every exception of a hub method as Error, a HubException (the hub's answer) too: CancelledHubCalls logs them instead.
        services.Configure<LoggerFilterOptions>(options => options.AddFilter("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", LogLevel.None));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<LiveSubscriptions>();
        services.AddSingleton<LiveRelay>();
        services.AddSingleton<LiveThrottle>();
        services.AddSingleton<ViewSessions>();
        // The platform's own channels: objects of every type, subscribed by view sessions, and each account's own.
        services.AddSingleton<ILiveChannel, ObjectLiveChannel>();
        services.AddSingleton<ILiveChannel, UserLiveChannel>();
        services.AddSingleton<IAccessChangeObserver>(provider => provider.GetRequiredService<LiveRelay>());
        services.AddHostedService<LiveRefreshService>();
        return services;
    }

    public static void MapLive(this IEndpointRouteBuilder app)
    {
        app.MapHub<LiveHub>("/hubs/live").RequireAuthorization();
    }
}
