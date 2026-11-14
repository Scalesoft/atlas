using FluentMigrator;

namespace Atlas.Server.Features.PageLinks.Persistence;

[Migration(2026100420, "Page link projection of published revisions")]
public sealed class PageLinkMigration() : EmbeddedSchemaMigration("2026100420_page_links.sql");
