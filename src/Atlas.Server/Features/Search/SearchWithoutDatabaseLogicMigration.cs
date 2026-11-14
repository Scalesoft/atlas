using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026110935, "Search without logic in the database: its triggers and functions go, C# writes and reads the same documents")]
public sealed class SearchWithoutDatabaseLogicMigration() : EmbeddedSchemaMigration("2026110935_search_without_database_logic.sql");
