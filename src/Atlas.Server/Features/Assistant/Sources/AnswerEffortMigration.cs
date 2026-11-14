using FluentMigrator;

namespace Atlas.Server.Features.Assistant.Sources;

[Migration(2026101270, "Chunks of one answer source page by effort")]
public sealed class AnswerEffortMigration() : EmbeddedSchemaMigration("2026101270_answer_effort.sql");
