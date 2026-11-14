using FluentMigrator;

namespace Atlas.Server.Features.Auth.Persistence;

[Migration(2026110540, "Release notes an account has seen (Co je nového)")]
public sealed class UserReleaseNotesMigration() : EmbeddedSchemaMigration("2026110540_user_release_notes.sql");
