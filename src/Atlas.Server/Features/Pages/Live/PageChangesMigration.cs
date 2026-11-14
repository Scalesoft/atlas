using FluentMigrator;

namespace Atlas.Server.Features.Pages.Live;

[Migration(2026102630, "Page changes announced to the live page channel")]
public sealed class PageChangesMigration() : EmbeddedSchemaMigration("2026102630_page_changes.sql");
