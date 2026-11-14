using FluentMigrator;

namespace Atlas.Server.Features.Auth.Persistence;

[Migration(2026100730, "User AI answer preferences: answer in search and default assistant scope")]
public sealed class UserAiAnswerPreferencesMigration() : EmbeddedSchemaMigration("2026100730_user_ai_answer_preferences.sql");
