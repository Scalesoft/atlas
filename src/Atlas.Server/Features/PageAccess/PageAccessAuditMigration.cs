using FluentMigrator;

namespace Atlas.Server.Features.PageAccess;

[Migration(2026092505)]
public sealed class PageAccessAuditMigration() : EmbeddedSchemaMigration("2026092505_page_access_audit.sql");
