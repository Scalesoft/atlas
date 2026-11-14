using FluentMigrator;

namespace Atlas.Server.Features.ModuleSettings;

[Migration(2026092501)]
public sealed class OidcAdminMappingMigration() : EmbeddedSchemaMigration("2026092501_oidc_admin_mapping.sql");
