-- The "AI s nástroji" right (D17, D20) and the daily limit of assistant.agent. Policy rows are temporal and immutable
-- (TR_*_Immutable): ADD COLUMN with a constant default fills existing rows without an UPDATE, so neither the history guard nor
-- the audit trigger fires and no audit event is invented. Existing and new group grants have tools on; personal rows inherit.
ALTER TABLE "GroupAiPolicy"
    ADD COLUMN "ToolsEnabled" boolean NOT NULL DEFAULT true,
    ADD COLUMN "AgentDailyLimit" integer NULL DEFAULT NULL
        CONSTRAINT "CK_GroupAiPolicy_AgentLimit" CHECK ("AgentDailyLimit" IS NULL OR "AgentDailyLimit" BETWEEN 0 AND 10000);
ALTER TABLE "UserAiPolicy"
    ADD COLUMN "ToolsEnabled" boolean NULL DEFAULT NULL,
    ADD COLUMN "AgentDailyLimit" integer NULL DEFAULT NULL
        CONSTRAINT "CK_UserAiPolicy_AgentLimit" CHECK ("AgentDailyLimit" IS NULL OR "AgentDailyLimit" BETWEEN 0 AND 10000);
