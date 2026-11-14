using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026100430, "Similar pages for a title being created")]
public sealed class SimilarPagesMigration() : EmbeddedSchemaMigration("2026100430_similar_pages.sql");
