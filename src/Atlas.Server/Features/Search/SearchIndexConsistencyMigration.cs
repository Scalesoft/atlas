using FluentMigrator;

namespace Atlas.Server;

[Migration(20, "Predictable search index response after publication and bulk import")]
public sealed class SearchIndexConsistencyMigration() : EmbeddedSchemaMigration("020_search_index_consistency.sql");
