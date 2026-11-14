using FluentMigrator;

namespace Atlas.Server.Features.Search;

[Migration(2026100550, "Hybrid tiered search over the knowledge index and did-you-mean suggestions")]
public sealed class HybridSearchMigration() : EmbeddedSchemaMigration("2026100550_hybrid_search.sql");
