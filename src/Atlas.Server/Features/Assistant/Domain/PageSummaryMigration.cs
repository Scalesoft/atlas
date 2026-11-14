using FluentMigrator;

namespace Atlas.Server.Features.Assistant.Domain;

[Migration(2026100820, "Page summary origin and persistent source coverage")]
public sealed class PageSummaryMigration() : EmbeddedSchemaMigration("2026100820_page_summary.sql");
