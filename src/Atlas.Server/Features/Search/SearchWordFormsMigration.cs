using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026102030, "Search word forms: Czech stems and code identifier parts as extra lexemes of every search vector")]
public sealed class SearchWordFormsMigration() : EmbeddedSchemaMigration("2026102030_search_word_forms.sql");
