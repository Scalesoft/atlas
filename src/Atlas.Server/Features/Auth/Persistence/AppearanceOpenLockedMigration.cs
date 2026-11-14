using FluentMigrator;

namespace Atlas.Server.Features.Auth.Persistence;

[Migration(2026110560, "The profile choice to open pages locked")]
public sealed class AppearanceOpenLockedMigration() : EmbeddedSchemaMigration("2026110560_appearance_open_locked.sql");
