using FluentMigrator;

namespace Atlas.Server.Features.AiIndex;

[Migration(2026100520, "Required pgvector extension")]
public sealed class PgvectorMigration() : EmbeddedSchemaMigration("2026100520_pgvector.sql");
