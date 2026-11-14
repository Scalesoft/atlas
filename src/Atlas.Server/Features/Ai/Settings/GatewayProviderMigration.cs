using FluentMigrator;

namespace Atlas.Server.Features.Ai.Settings;

[Migration(2026100840, "Company Gateway chat and quick provider")]
public sealed class GatewayProviderMigration() : EmbeddedSchemaMigration("2026100840_gateway_provider.sql");

[Migration(2026111400, "The company provider's revisions stored as gateway")]
public sealed class GatewayProviderIdMigration() : EmbeddedSchemaMigration("2026111400_gateway_provider_id.sql");
