using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Platform.Backup;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Backups.Installation;

/// <summary>Stores the backup key on the backup settings row, protected by the host's Data Protection: the host needs it for every
/// export, the administrator keeps the shown copy for a restore on another host.</summary>
public sealed class BackupKeys(IDataProtectionProvider protection)
{
    public const string Purpose = "ScaleAtlas.Backup.Key.v1";
    private readonly IDataProtector m_protector = protection.CreateProtector(Purpose);

    public async Task<BackupKey?> CurrentAsync(ISession session, CancellationToken ct)
        => (await session.GetAsync<BackupSettings>(1, ct))?.ProtectedBackupKey is { } stored ? BackupKey.Parse(m_protector.Unprotect(stored)) : null;

    /// <summary>A new key on the settings row the caller holds for update; the old one stops encrypting at the next export.</summary>
    public BackupKey Replace(BackupSettings settings)
    {
        var key = BackupKey.Generate();
        settings.ProtectedBackupKey = m_protector.Protect(key.Text);
        settings.BackupKeyId = key.Id;
        settings.BackupKeyCreatedAt = DateTime.UtcNow;
        return key;
    }
}
