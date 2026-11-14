using FluentMigrator;

namespace Atlas.Server.Features.Auth.Persistence;

[Migration(2026110160, "Saving the OIDC providers and issuing a backup key are audited security changes")]
public sealed class SecuritySettingsAuditMigration() : EmbeddedSchemaMigration("2026110160_security_settings_audit.sql");
