using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026102720, "Search: an earlier page title (alias) ranks above a heading inside a page")]
public sealed class SearchAliasAboveHeadingsMigration() : EmbeddedSchemaMigration("2026102720_search_alias_above_headings.sql");
