using FluentMigrator;

namespace Atlas.Server.Features.PageLinks.Persistence;

[Migration(2026100560, "Related pages: precomputed page neighbours per index generation")]
public sealed class PageNeighborMigration() : EmbeddedSchemaMigration("2026100560_page_neighbors.sql");
