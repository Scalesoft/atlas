using FluentMigrator;

namespace Atlas.Server.Features.ModuleSettings;

[Migration(14)]
public sealed class OidcSettingsMigration() : EmbeddedSchemaMigration("014_oidc_settings.sql");
