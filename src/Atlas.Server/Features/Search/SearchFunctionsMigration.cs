using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(27, "Search ranking and access filters as schema functions")]
public sealed class SearchFunctionsMigration() : EmbeddedSchemaMigration("027_search_functions.sql");

[Migration(28, "Unified default search scope across published pages and drafts")]
public sealed class SearchAllMigration() : EmbeddedSchemaMigration("028_search_all.sql");
