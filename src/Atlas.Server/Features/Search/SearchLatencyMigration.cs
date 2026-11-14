using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026102873, "Search latency: lexemes keep their plan cache, heading and alias checks behind the page's union vector, fuzzy body only when it can reach the window")]
public sealed class SearchLatencyMigration() : EmbeddedSchemaMigration("2026102873_search_latency.sql");
