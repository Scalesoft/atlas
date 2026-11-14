using FluentMigrator;

namespace Atlas.Server.Features.Vaults.Release;

[Migration(2026092720, "Access requests and audited secret release")]
public sealed class SecretReleaseMigration() : EmbeddedSchemaMigration("2026092720_secret_release.sql");
