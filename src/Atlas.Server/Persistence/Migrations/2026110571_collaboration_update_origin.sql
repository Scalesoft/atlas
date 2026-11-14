-- Atlas 3.6-H: where each live update came from. A browser's update keeps all three empty (web); a write of an agent (MCP, the personal
-- API, the in-process assistant) names its channel, the personal API token and the assistant conversation. Existing rows stay web.
ALTER TABLE "CollaborationUpdate" ADD COLUMN "Channel" text NULL CHECK ("Channel" IN ('api', 'mcp', 'assistant', 'web', 'system')),
    ADD COLUMN "ApiTokenId" uuid NULL, ADD COLUMN "AiConversationId" uuid NULL,
    ADD CONSTRAINT "CK_CollaborationUpdate_Origin" CHECK (("ApiTokenId" IS NULL OR "Channel" IN ('api', 'mcp'))
        AND ("AiConversationId" IS NULL OR "Channel" = 'assistant'));
