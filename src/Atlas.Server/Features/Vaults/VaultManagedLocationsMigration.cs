using FluentMigrator;

namespace Atlas.Server.Features.Vaults;

[Migration(2026092800, "Vault locations managed by Atlas in its data directory")]
public sealed class VaultManagedLocationsMigration() : EmbeddedSchemaMigration("2026092800_vault_managed_locations.sql");
