using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026102880, "Search: the alias tier above headings over the search latency bodies")]
public sealed class SearchAliasAfterLatencyMigration() : EmbeddedSchemaMigration("2026102880_search_alias_after_latency.sql");
