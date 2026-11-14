using FluentMigrator;

namespace Atlas.Server.Features.Security;

[Migration(2026092830, "Audit of security changes covered by the re-authentication window")]
public sealed class ReauthWindowMigration() : EmbeddedSchemaMigration("2026092830_reauth_window.sql");
