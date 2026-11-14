using FluentMigrator;

namespace Atlas.Server.Features.Auth.Persistence;

[Migration(2026100601, "Durable browser session revocation")]
public sealed class BrowserSessionRevocationMigration() : EmbeddedSchemaMigration("2026100601_browser_session_revocation.sql");
