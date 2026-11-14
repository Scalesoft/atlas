using FluentMigrator;

namespace Atlas.Server.Features.Auth.Persistence;

[Migration(2026100580, "Per-user AI preferences")]
public sealed class UserAiPreferenceMigration() : EmbeddedSchemaMigration("2026100580_user_ai_preferences.sql");
