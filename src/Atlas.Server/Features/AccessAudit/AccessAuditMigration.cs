using FluentMigrator;

namespace Atlas.Server.Features.AccessAudit;

[Migration(22, "Attributed access and credential lifecycle audit")]
public sealed class AccessAuditMigration() : EmbeddedSchemaMigration("022_access_audit.sql");
