using FluentMigrator;

namespace Atlas.Server.Features.PersonalApi.Persistence;

[Migration(2026102020, "Personal API level and scope direct: MCP publication without the web confirmation")]
public sealed class ApiDirectPublishMigration() : EmbeddedSchemaMigration("2026102020_api_direct_publish.sql");
