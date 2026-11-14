using FluentMigrator;

namespace Atlas.Server.Features.PageDecorations.Persistence;

[Migration(2026092901, "Page decoration appearance catalog")]
public sealed class PageDecorationPaletteMigration() : EmbeddedSchemaMigration("2026092901_page_decoration_palette.sql");
