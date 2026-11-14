using FluentMigrator;

namespace Atlas.Server.Features.ApiActivity.Persistence;

[Migration(2026100620, "Audit of writes over the personal API and MCP; MCP module setting")]
public sealed class ApiActivityMigration() : EmbeddedSchemaMigration("2026100620_api_activity_audit.sql");
