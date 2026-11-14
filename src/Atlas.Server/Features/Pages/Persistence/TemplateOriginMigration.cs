using FluentMigrator;

namespace Atlas.Server.Features.Pages.Persistence;

[Migration(2026103180, "The template a page was created from")]
public sealed class TemplateOriginMigration() : EmbeddedSchemaMigration("2026103180_page_template_origin.sql");
