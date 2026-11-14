using FluentMigrator;

namespace Atlas.Server;

[Migration(21, "Bounded published title and import path search aliases")]
public sealed class SearchAliasesMigration() : EmbeddedSchemaMigration("021_search_aliases.sql");
