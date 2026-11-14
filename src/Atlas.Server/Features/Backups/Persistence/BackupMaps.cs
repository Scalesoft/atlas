using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Features.Backups.Persistence;

public sealed class BackupSettingsMap : ClassMapping<BackupSettings>
{
    public BackupSettingsMap()
    {
        Table("`BackupSettings`");
        Id(x => x.Id, m => { m.Column("`Id`"); m.Generator(Generators.Assigned); });
        Property(x => x.Enabled);
        Property(x => x.IntervalMinutes);
        Property(x => x.Mode);
        Property(x => x.Destination);
        Property(x => x.Username);
        Property(x => x.Domain);
        Property(x => x.ProtectedPassword);
        Property(x => x.StorageProfileId);
        Property(x => x.Folder);
        Property(x => x.RetentionCount);
        Property(x => x.NextRunAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.ProtectedBackupKey);
        Property(x => x.BackupKeyId);
        Property(x => x.BackupKeyCreatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
public sealed class BackupRunMap : ClassMapping<BackupRun>
{
    public BackupRunMap()
    {
        Table("`BackupRun`");
        Id(x => x.Id, m => { m.Column("`Id`"); m.Generator(Generators.Assigned); });
        Property(x => x.SettingsId);
        Property(x => x.StartedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.FinishedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.Status);
        Property(x => x.Trigger);
        Property(x => x.Mode);
        Property(x => x.Destination);
        Property(x => x.ContentToken);
        Property(x => x.FileName);
        Property(x => x.Size);
        Property(x => x.Message);
    }
}
