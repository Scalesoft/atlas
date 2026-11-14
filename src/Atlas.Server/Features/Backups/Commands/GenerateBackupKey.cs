using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Features.ModuleSettings;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Backups.Commands;

/// <summary>Creates the backup key, or replaces it (rotation). The response is the only place the key is ever shown; backups
/// written before keep needing the old key. With a complete backup the key opens every vault password in it (docs/git-backup.md), so
/// issuing one is a backup destination change: a fresh sign-in and the audit event of the kind backup.key (SEC-T-3).</summary>
public sealed record GenerateBackupKey;

public static class GenerateBackupKeyHandler
{
    public static async Task<BackupKeyCreated> Handle(GenerateBackupKey command, ModuleSettingsAccess access, ISecurityChangeGuard guard, ISession session,
        BackupKeys keys, CancellationToken ct)
    {
        var actor = await access.RequireWikiAdminAsync(ct);
        await guard.RequireAsync(actor, new SecurityChange("backup.destination", Guid.Empty), ct);
        var settings = await session.GetAsync<BackupSettings>(1, LockMode.Upgrade, ct);
        var key = keys.Replace(settings);
        await AuthorizeBackupDestinationHandler.RecordAsync(session, actor.Id, AuthorizeBackupDestinationHandler.BackupKey, key.Id, ct);
        return new BackupKeyCreated(key.Text, key.Id, settings.BackupKeyCreatedAt!.Value);
    }
}
