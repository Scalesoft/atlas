using FluentMigrator;

namespace Atlas.Server.Features.Pages;

[Migration(2026092703, "Template flag on ordinary pages")]
public sealed class PageTemplateFlagMigration() : EmbeddedSchemaMigration("2026092703_page_templates.sql");
