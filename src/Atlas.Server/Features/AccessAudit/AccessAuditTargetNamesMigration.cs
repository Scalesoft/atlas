using FluentMigrator;

namespace Atlas.Server.Features.AccessAudit;

[Migration(2026092740, "Access audit names the vault and secret of an event")]
public sealed class AccessAuditTargetNamesMigration() : EmbeddedSchemaMigration("2026092740_access_audit_target_names.sql");
