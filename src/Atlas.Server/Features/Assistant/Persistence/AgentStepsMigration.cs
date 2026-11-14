using FluentMigrator;

namespace Atlas.Server.Features.Assistant.Persistence;

[Migration(2026101620, "The tool steps of an agent turn on its answer")]
public sealed class AgentStepsMigration() : EmbeddedSchemaMigration("2026101620_agent_steps.sql");
