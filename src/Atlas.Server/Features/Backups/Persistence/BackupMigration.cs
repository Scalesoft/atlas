using FluentMigrator;
namespace Atlas.Server.Features.Backups.Persistence;

[Migration(2026091001)]
public sealed class BackupMigration() : EmbeddedSchemaMigration("2026091001_backups.sql");

[Migration(2026102725, "Backup key and the complete ZIP mode")]
public sealed class BackupKeyMigration() : EmbeddedSchemaMigration("2026102725_backup_key.sql");

[Migration(2026102996, "Changing a backup destination is a security change with an access audit event")]
public sealed class BackupDestinationAuditMigration() : EmbeddedSchemaMigration("2026102996_backup_destination_audit.sql");

[Migration(2026103010, "Switching to the complete mode and downloading a complete archive are audited; the audit view names the destination")]
public sealed class BackupAuditViewMigration() : EmbeddedSchemaMigration("2026103010_backup_audit_view.sql");

[Migration(2026110170, "An archive import announces its publications as a rebuild, which tells nobody of a mention again")]
public sealed class ImportPublicationRebuildMigration() : EmbeddedSchemaMigration("2026110170_import_publication_rebuild.sql");

[Migration(2026110172, "The ZIP backup holds the whole installation by default")]
public sealed class BackupCompleteDefaultMigration() : EmbeddedSchemaMigration("2026110172_backup_complete_default.sql");

[Migration(2026110460, "The backups run as jobs: the ZIP checks become runs of backups.zip, the Git retry counter goes")]
public sealed class BackupJobRunsMigration() : EmbeddedSchemaMigration("2026110460_backup_job_runs.sql");
