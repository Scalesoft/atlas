using FluentMigrator;

namespace Atlas.Server.Features.Vaults.Release;

[Migration(2026092910, "The page a secret was released on")]
public sealed class SecretReleasePageMigration() : EmbeddedSchemaMigration("2026092910_secret_release_page.sql");
