using FluentMigrator;

namespace Atlas.Server.Features.Vaults;

[Migration(2026092702, "Vault and secret access rights")]
public sealed class VaultAccessMigration() : EmbeddedSchemaMigration("2026092702_vault_access.sql");
