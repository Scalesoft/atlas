-- 3.6: a token with the scope write publishes at once (2026110684), so the web confirmation of a publication over MCP is gone. Its
-- requests expired ten minutes after they were made and were never backed up; nothing else refers to them.
DROP TABLE IF EXISTS "McpPublishRequest";
DROP FUNCTION IF EXISTS atlas_mcp_publish_request_guard();
