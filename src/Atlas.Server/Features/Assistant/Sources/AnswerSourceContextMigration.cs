using FluentMigrator;
using Atlas.Server.Platform.Persistence;

namespace Atlas.Server.Features.Assistant.Sources;

[Migration(2026100860, "Coherent answer context for short sections")]
public sealed class AnswerSourceContextMigration() : EmbeddedSchemaMigration("2026100860_answer_source_context.sql");