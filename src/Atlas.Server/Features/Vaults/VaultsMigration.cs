using FluentMigrator;

namespace Atlas.Server.Features.Vaults;

[Migration(2026092701, "KeePass vault registry")]
public sealed class VaultsMigration() : EmbeddedSchemaMigration("2026092701_vaults.sql");
