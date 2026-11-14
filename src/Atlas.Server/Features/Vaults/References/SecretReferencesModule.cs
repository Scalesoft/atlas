using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atlas.Server.Features.Vaults.References;

public static class SecretReferencesModule
{
    /// <summary>Reference decisions every content path needs (pages, history, export, collaboration), with or without the vault
    /// runtime: only the registry and grant tables are read.</summary>
    public static IServiceCollection AddSecretReferences(this IServiceCollection services)
    {
        services.TryAddScoped<VaultGrantSnapshots>();
        services.TryAddScoped<SecretReferenceAccess>();
        return services;
    }
}
