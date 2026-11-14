using FluentMigrator;

namespace Atlas.Server.Features.SpaceAi.Persistence;

[Migration(2026100530, "Space inclusion in AI and atlas_space_in_ai")]
public sealed class SpaceAiInclusionMigration() : EmbeddedSchemaMigration("2026100530_space_ai_inclusion.sql");
