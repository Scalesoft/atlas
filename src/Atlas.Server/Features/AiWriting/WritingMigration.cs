using FluentMigrator;

namespace Atlas.Server.Features.AiWriting;

[Migration(2026100800, "Informational AI assistance flag on page revisions")]
public sealed class WritingMigration() : EmbeddedSchemaMigration("2026100800_ai_assisted_revision.sql");
