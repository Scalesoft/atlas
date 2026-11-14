using FluentMigrator;

namespace Atlas.Server.Features.Ai.Settings.SavedProfiles;

[Migration(2026100870, "Named AI profile library with explicit activation")]
public sealed class AiSavedProfilesMigration() : EmbeddedSchemaMigration("2026100870_ai_saved_profiles.sql");
