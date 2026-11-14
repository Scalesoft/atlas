using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026092621, "Item access in search")]
public sealed class ItemAccessSearchMigration() : EmbeddedSchemaMigration("2026092621_item_access_search.sql");
