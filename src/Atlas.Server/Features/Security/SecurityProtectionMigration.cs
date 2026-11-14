using FluentMigrator;

namespace Atlas.Server.Features.Security;

[Migration(2026092704, "Protection modes, trusted networks and proxies, fresh re-authentication")]
public sealed class SecurityProtectionMigration() : EmbeddedSchemaMigration("2026092704_security_protection.sql");
