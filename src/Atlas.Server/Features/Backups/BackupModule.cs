using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Backups.Archive;
using Atlas.Server.Features.Backups.Git;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Backups;

public static class BackupModule
{
    public static IServiceCollection AddWikiBackups(this IServiceCollection services)
    {
        services.AddSingleton<WikiBackupArchive>();
        services.AddSingleton<WikiBackupImport>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<BackupImportGate>();
        services.AddJob<ZipBackupJob>();
        services.AddSingleton<GitBackupSync>();
        services.AddSingleton<GitBackupService>();
        services.AddJob<GitBackupJob>();
        // Format v7 (docs/backup-format-v7.md). The module backups use platform services only, never those of a running module,
        // so a disabled module is backed up and restored too (plan 5.4).
        services.AddSingleton(provider => InstallationModules.Catalog(provider.GetRequiredService<IDataProtectionProvider>(),
            provider.GetRequiredService<AttachmentFiles>(),
            new VaultFolders(provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IWebHostEnvironment>()),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("Atlas.Server.Features.Backups.Installation")));
        services.AddSingleton<BackupKeys>();
        services.AddSingleton<InstallationExport>();
        services.AddSingleton<InstallationArchive>();
        services.AddSingleton<InstallationRestore>();
        return services;
    }
}

public sealed class BackupImportGate : IDisposable
{
    private readonly SemaphoreSlim m_gate = new(1, 1);
    public Task WaitAsync(CancellationToken ct) => m_gate.WaitAsync(ct);
    public void Release() => m_gate.Release();
    public void Dispose() => m_gate.Dispose();
}

public sealed record RequireBackupAdministrator;
public sealed record RevalidateBackupDownload;
public static class RequireBackupAdministratorHandler
{
    public static async Task<Guid> Handle(RequireBackupAdministrator message, ModuleSettingsAccess access, CancellationToken ct)
        => (await access.RequireWikiAdminAsync(ct)).Id;

    public static async Task<DownloadLease> Handle(RevalidateBackupDownload message, ModuleSettingsAccess access, NHibernate.ISession session, CancellationToken ct)
    {
        var actor = await access.RequireWikiAdminAsync(ct);
        var authority = (await EffectiveWikiAdmin.ReadAsync(session, actor, ct)).Authority(DateTime.UtcNow);
        if (!authority.Allowed) throw new ApiException(403, "Platnost oprávnění ke stažení skončila.");
        return new DownloadLease(authority.ExpiresAt);
    }
}
