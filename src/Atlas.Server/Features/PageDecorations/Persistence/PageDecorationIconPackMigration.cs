using FluentMigrator;

namespace Atlas.Server.Features.PageDecorations.Persistence;

[Migration(2026101141, "Page icons from the whole icon pack")]
public sealed class PageDecorationIconPackMigration() : EmbeddedSchemaMigration("2026101141_page_decoration_icon_pack.sql");
