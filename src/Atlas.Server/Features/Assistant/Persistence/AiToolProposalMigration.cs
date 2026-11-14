using FluentMigrator;

namespace Atlas.Server.Features.Assistant.Persistence;

[Migration(2026101630, "Proposals of agent writes awaiting the user's confirmation")]
public sealed class AiToolProposalMigration() : EmbeddedSchemaMigration("2026101630_ai_tool_proposal.sql");
