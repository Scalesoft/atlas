using FluentMigrator;

namespace Atlas.Server.Features.Ai.Access;

[Migration(2026100850, "Temporal group AI grants and personal exceptions")]
public sealed class AiAccessPoliciesMigration() : EmbeddedSchemaMigration("2026100850_ai_access_policies.sql");