using FluentMigrator;

namespace Atlas.Server.Features.PageSource.Persistence;

[Migration(2026092620, "Page source edit audit action")]
public sealed class PageSourceAuditMigration() : EmbeddedSchemaMigration("2026092620_page_source_audit.sql");
