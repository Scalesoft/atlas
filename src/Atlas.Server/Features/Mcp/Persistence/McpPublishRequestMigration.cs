using FluentMigrator;

namespace Atlas.Server.Features.Mcp.Persistence;

[Migration(2026100630, "Publication over MCP confirmed by a person in the web")]
public sealed class McpPublishRequestMigration() : EmbeddedSchemaMigration("2026100630_mcp_publish_request.sql");
