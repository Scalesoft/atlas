using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026110686, "Search: the snippet reads a 20 000-character window of the body around the first match")]
public sealed class SearchSnippetWindowMigration() : EmbeddedSchemaMigration("2026110686_search_snippet_window.sql");
