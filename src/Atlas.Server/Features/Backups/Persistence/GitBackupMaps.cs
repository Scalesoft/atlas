using FluentMigrator;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Backups.Git;

namespace Atlas.Server.Features.Backups.Persistence;

[Migration(2026092960, "Continuous git backup")]
public sealed class GitBackupMigration() : EmbeddedSchemaMigration("2026092960_git_backup.sql");

public sealed class GitBackupSettingsMap : ClassMapping<GitBackupSettings>
{
    public GitBackupSettingsMap()
    {
        Table("`GitBackupSettings`");
        Id(x => x.Id, m => { m.Column("`Id`"); m.Generator(Generators.Assigned); });
        Property(x => x.Enabled);
        Property(x => x.RemoteUrl);
        Property(x => x.Branch);
        Property(x => x.RepositoryId);
        Property(x => x.ProtectedPrivateKey);
        Property(x => x.PublicKey);
        Property(x => x.KnownHosts);
        Property(x => x.LastSyncAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.LastCommit);
        Property(x => x.LastError);
        Property(x => x.LastErrorAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}

public sealed class GitBackupPageMap : ClassMapping<GitBackupPage>
{
    public GitBackupPageMap()
    {
        Table("`GitBackupPage`");
        Id(x => x.PageId, m => { m.Column("`PageId`"); m.Generator(Generators.Assigned); });
        Property(x => x.Path);
        Property(x => x.Version);
        Property(x => x.PublishedVersion);
    }
}
