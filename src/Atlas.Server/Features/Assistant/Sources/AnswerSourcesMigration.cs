using FluentMigrator;

namespace Atlas.Server.Features.Assistant.Sources;

[Migration(2026100740, "Sources of an answer from the wiki and their status")]
public sealed class AnswerSourcesMigration() : EmbeddedSchemaMigration("2026100740_answer_sources.sql");
