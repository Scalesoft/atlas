using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026102890, "Search: the fuzzy body test reads the head of a page, and hybrid search skips it behind full strict hits")]
public sealed class SearchBodyHeadMigration() : EmbeddedSchemaMigration("2026102890_search_body_head.sql");
