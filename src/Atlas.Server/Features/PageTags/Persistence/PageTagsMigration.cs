using FluentMigrator;

namespace Atlas.Server.Features.PageTags.Persistence;

[Migration(29, "Shared page tags and temporal assignments")]
public sealed class PageTagsMigration() : EmbeddedSchemaMigration("029_page_tags.sql");
