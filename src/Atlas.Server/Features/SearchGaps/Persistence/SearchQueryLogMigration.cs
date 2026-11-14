using FluentMigrator;

namespace Atlas.Server.Features.SearchGaps.Persistence;

[Migration(2026100440, "Search query log for zero-result queries")]
public sealed class SearchQueryLogMigration() : EmbeddedSchemaMigration("2026100440_search_query_log.sql");
