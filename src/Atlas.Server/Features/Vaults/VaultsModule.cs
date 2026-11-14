using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.Vaults.Kdbx;
using Atlas.Server.Features.Vaults.Release;
using Atlas.Server.Features.Vaults.Runtime;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Vaults;

public static class VaultsModule
{
    public static IServiceCollection AddVaults(this IServiceCollection services, IConfiguration configuration)
    {
        var options = VaultOptions.Read(configuration);
        services.AddSingleton(options);
        // One instance per process: key derivation is single flight across all vaults.
        services.AddSingleton<KdbxKeyDerivation>();
        services.AddSingleton<KdbxReader>();
        services.AddSingleton<KdbxWriter>();
        services.AddSingleton<VaultProtection>();
        services.AddSingleton<VaultFolders>();
        services.AddSingleton<VaultFiles>();
        // Keys and snapshots live for the process; the keyring zeroes its keys when the container disposes it.
        services.AddSingleton<VaultKeyring>();
        services.AddSingleton<VaultSnapshots>();
        services.AddSingleton<VaultRefreshService>();
        if (options.RefreshSeconds > 0)
        {
            services.AddJob<VaultRefreshJob>();
        }
        services.AddScoped<VaultGrantSnapshots>();
        services.AddScoped<VaultAccess>();
        services.AddScoped<VaultOperations>();
        services.AddVaultRelease(configuration);
        services.AddScoped<IAccessKind, VaultAccessKind>();
        services.AddScoped<IAccessKind, SecretAccessKind>();
        return services;
    }
}
