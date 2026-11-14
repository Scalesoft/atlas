using FluentMigrator;

namespace Atlas.Server.Features.PageDecorations.Persistence;

[Migration(30, "Optional shared page icons and colors")]
public sealed class PageDecorationsMigration() : EmbeddedSchemaMigration("030_page_decorations.sql");
