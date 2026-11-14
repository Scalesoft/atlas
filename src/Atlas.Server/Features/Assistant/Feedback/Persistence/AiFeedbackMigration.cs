using FluentMigrator;

namespace Atlas.Server.Features.Assistant.Feedback.Persistence;

[Migration(2026100710, "AI feedback: one anonymous rating per answer")]
public sealed class AiFeedbackMigration() : EmbeddedSchemaMigration("2026100710_ai_feedback.sql");
