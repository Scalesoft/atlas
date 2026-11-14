using FluentMigrator;

namespace Atlas.Server.Features.Mcp.Persistence;

[Migration(2026110685, "3.6: drop the web confirmation of MCP publications (write publishes at once)")]
public sealed class DropMcpPublishRequestMigration() : EmbeddedSchemaMigration("2026110685_drop_mcp_publish_request.sql");
