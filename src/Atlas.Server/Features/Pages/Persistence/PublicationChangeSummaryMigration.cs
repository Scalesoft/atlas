using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026100410, "Change summary of a page publication")]
public sealed class PublicationChangeSummaryMigration() : EmbeddedSchemaMigration("2026100410_publication_change_summary.sql");
