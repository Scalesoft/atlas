using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atlas.Markdown.Sync;

/// <summary>Wire-up for the Atlas Markdown collab relay/persist backend.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the collab relay services (registry + 1 s flush service). The host must ALSO
    /// call <c>AddSignalR(o =&gt; o.MaximumReceiveMessageSize = 8 * 1024 * 1024)</c> and register
    /// its own <see cref="ICollabAuthenticator"/> and <see cref="IDocumentStore"/>.
    /// </summary>
    public static IServiceCollection AddAtlasMarkdownSync(
        this IServiceCollection services,
        Action<MarkdownSyncOptions>? configure = null)
    {
        var options = new MarkdownSyncOptions();
        configure?.Invoke(options);
        if (options.AccessRefreshInterval <= TimeSpan.Zero || options.AuthorizationTimeout <= TimeSpan.Zero ||
            options.MaxConcurrentAuthorizations < 1)
            throw new ArgumentOutOfRangeException(nameof(configure), "Access refresh and authorization timeout must be positive.");
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DocumentRegistry>();
        services.AddSingleton<DocumentAccessCoordinator>();
        services.AddSingleton<DocumentCompactor>();
        services.AddSingleton<LiveDocumentWriter>();
        services.TryAddSingleton(new DocumentAuthorityOptions());
        services.AddSingleton<DocumentAuthorityCoordinator>();
        services.AddHostedService<DocumentAccessRefreshService>();
        services.AddSingleton<MarkdownSyncMetrics>();
        services.AddHostedService<DocumentFlushService>();
        services.TryAddSingleton<IDocumentContentGuard, PassThroughDocumentContentGuard>();
        return services;
    }

    /// <summary>
    /// Opt in to managed Yjs content validation (<see cref="ManagedDocumentContentGuard"/>): every update, snapshot and
    /// seed is decoded and checked against <paramref name="policy"/> (default: the embedded editor manifest) before the
    /// hub stores or broadcasts it. Reserved nodes are rejected unless the host registers an <see cref="IReservedNodeGuard"/>.
    /// Call before or after <see cref="AddAtlasMarkdownSync"/>.
    /// </summary>
    public static IServiceCollection AddAtlasMarkdownContentGuard(this IServiceCollection services, YjsContentPolicy? policy = null)
    {
        services.Replace(ServiceDescriptor.Singleton(policy ?? YjsContentPolicy.CreateDefault()));
        services.Replace(ServiceDescriptor.Singleton<IDocumentContentGuard, ManagedDocumentContentGuard>());
        return services;
    }

    /// <summary>Map the Markdown sync hub at <paramref name="pattern"/>.</summary>
    public static HubEndpointConventionBuilder MapAtlasMarkdownSyncHub(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/hubs/markdown")
        => endpoints.MapHub<MarkdownSyncHub>(pattern);
}
