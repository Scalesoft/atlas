using FluentMigrator;

namespace Atlas.Server.Features.PersonalLibrary.Persistence;

[Migration(32, "Durable page views and revision activity indexes")]
public sealed class PageViewsMigration() : EmbeddedSchemaMigration("032_page_views.sql");
