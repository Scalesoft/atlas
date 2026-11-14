using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026102895, "Search: the fuzzy body test has its own CTE, so the trigram index of the body head serves it")]
public sealed class SearchBodyCteMigration() : EmbeddedSchemaMigration("2026102895_search_body_cte.sql");
