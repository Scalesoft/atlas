using FluentMigrator;

namespace Atlas.Server.Features.Ai.Settings;

[Migration(2026100510, "AI configuration: master switch, immutable profile revisions, task switches and usage")]
public sealed class AiPlatformMigration() : EmbeddedSchemaMigration("2026100510_ai_platform.sql");
