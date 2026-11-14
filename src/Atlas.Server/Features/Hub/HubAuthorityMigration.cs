using FluentMigrator;

namespace Atlas.Server.Features.Hub;

[Migration(2026100102)]
public sealed class HubAuthorityMigration() : EmbeddedSchemaMigration("2026100102_hub_authority.sql");
