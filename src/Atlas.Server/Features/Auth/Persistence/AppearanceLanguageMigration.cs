using FluentMigrator;

namespace Atlas.Server.Features.Auth.Persistence;

[Migration(2026111100, "The profile choice of the language of the tool")]
public sealed class AppearanceLanguageMigration() : EmbeddedSchemaMigration("2026111100_appearance_language.sql");
