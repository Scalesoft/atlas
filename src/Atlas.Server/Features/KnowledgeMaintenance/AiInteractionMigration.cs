using FluentMigrator;

namespace Atlas.Server.Features.KnowledgeMaintenance;

[Migration(2026100830, "Anonymous daily AI adoption counts")]
public sealed class AiInteractionMigration() : EmbeddedSchemaMigration("2026100830_ai_interaction_daily.sql");
