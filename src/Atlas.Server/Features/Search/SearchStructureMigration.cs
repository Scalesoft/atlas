using FluentMigrator;

namespace Atlas.Server;

[Migration(16, "Published heading search projection")]
public sealed class SearchStructureMigration() : EmbeddedSchemaMigration("016_search_structure.sql");
