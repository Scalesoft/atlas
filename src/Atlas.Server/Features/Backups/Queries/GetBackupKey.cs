using Atlas.Server.Features.ModuleSettings;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Backups.Queries;

/// <summary>Whether a backup key exists, its id and when it was created; never the key.</summary>
public sealed record GetBackupKey;

public static class GetBackupKeyHandler
{
    public static async Task<BackupKeyStatus> Handle(GetBackupKey query, ModuleSettingsAccess access, ISession session, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var settings = await session.GetAsync<BackupSettings>(1, ct);
        return new BackupKeyStatus(settings.BackupKeyId, settings.BackupKeyCreatedAt);
    }
}
