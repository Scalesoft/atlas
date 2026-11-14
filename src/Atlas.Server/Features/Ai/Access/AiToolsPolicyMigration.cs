using FluentMigrator;

namespace Atlas.Server.Features.Ai.Access;

[Migration(2026101605, "The AI with tools right and the agent's daily limit")]
public sealed class AiToolsPolicyMigration() : EmbeddedSchemaMigration("2026101605_ai_tools_policy.sql");
