using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Security.Networks;
using Atlas.Server.Features.Security.Queries;
using Atlas.Server.Features.Security.Reauth;

namespace Atlas.Server.Features.Security;

public static class SecurityModule
{
    /// <summary>Protection modes, trusted networks and proxies, fresh re-authentication and the real security change guard. Needs
    /// <see cref="TrustedProxyEnvironment"/> (AddAtlasHttpHosting) and AtlasOidcOptions (AddOpenIdConnect).</summary>
    public static IServiceCollection AddSecurityCenter(this IServiceCollection services)
    {
        services.AddSingleton<SecurityConfigurationCache>();
        services.AddScoped<FreshAuthMethods>();
        services.AddScoped<ReauthProofs>();
        services.Replace(ServiceDescriptor.Scoped<ISecurityChangeGuard, ReauthSecurityChangeGuard>());
        // Replace, not TryAdd: V5 registers a fail-closed fallback with TryAddScoped.
        services.AddScoped<ReleasePolicy>();
        services.Replace(ServiceDescriptor.Scoped<Vaults.Release.IReleasePolicy>(provider => provider.GetRequiredService<ReleasePolicy>()));
        services.AddScoped<ProtectionStates>();
        return services;
    }
}
