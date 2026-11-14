using FluentMigrator;

namespace Atlas.Server.Features.Assistant.Persistence;

[Migration(2026100880, "Preserve daily AI consumption when conversations are deleted")]
public sealed class AiDeletedDailyUsageMigration() : EmbeddedSchemaMigration("2026100880_ai_deleted_daily_usage.sql");
