using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026102010, "Search quality: OpenAPI sources in the search body and AI index, a title-word tier, no title-only passages")]
public sealed class SearchQualityMigration() : EmbeddedSchemaMigration("2026102010_search_quality.sql");
