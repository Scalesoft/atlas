-- Atlas 2.0 phase 2 A1: AI configuration. No rows means AI is off everywhere (AI-24); upgrades start empty, no backfill.
-- Module settings pattern (EditorAssistSetting, YouTrackConfiguration): one current temporal row per section, closed
-- intervals as history, ActiveFrom as the compare-and-swap version.

-- Master switch of AI for the whole wiki. No row = Vypnuto.
CREATE TABLE "AiSetting" (
    "Id" uuid PRIMARY KEY,
    "Enabled" boolean NOT NULL,
    "ChangedById" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz,
    CONSTRAINT "CK_AiSetting_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_AiSetting_Interval" EXCLUDE USING gist (tstzrange("ActiveFrom", "ActiveTo", '[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_AiSetting_Current" ON "AiSetting" ((true)) WHERE "ActiveTo" IS NULL;

-- Immutable revision of one profile kind (spec RAG §7). Every save inserts a row; the current one is chosen by
-- AiProfileAssignment. The API key is protected by IDataProtector "ScaleAtlas.Ai.ApiKey.v1" and never leaves the server.
-- Fingerprint (embeddings only) = SHA-256 of provider, model, model version, dimensions and prefixes: what decides whether
-- an index generation stays compatible. URL, key, timeout and concurrency are deliberately not part of it.
CREATE TABLE "AiProfileRevision" (
    "Id" uuid PRIMARY KEY,
    "Kind" varchar(16) NOT NULL CHECK ("Kind" IN ('chat', 'embeddings', 'quick')),
    "Enabled" boolean NOT NULL,
    "Provider" varchar(32) NOT NULL CHECK ("Provider" IN ('openai-compatible', 'anthropic')),
    "Location" varchar(16) NOT NULL CHECK ("Location" IN ('company', 'cloud')),
    "BaseUrl" varchar(2048) NULL,
    "Model" varchar(200) NOT NULL,
    "ModelVersion" varchar(200) NULL,
    "ProtectedApiKey" text NOT NULL DEFAULT '',
    "TimeoutSeconds" integer NOT NULL CHECK ("TimeoutSeconds" BETWEEN 1 AND 300),
    "MaxConcurrency" integer NOT NULL CHECK ("MaxConcurrency" BETWEEN 1 AND 16),
    "ReasoningEffort" varchar(8) NULL CHECK ("ReasoningEffort" IN ('none', 'low', 'medium', 'high')),
    "Temperature" real NULL CHECK ("Temperature" BETWEEN 0 AND 2),
    "NativeJsonSchema" boolean NOT NULL DEFAULT true,
    -- Phase 2 stores vector(1024) only (O3); another dimension is a new migration.
    "Dimensions" integer NULL CHECK ("Dimensions" = 1024),
    "QueryPrefix" varchar(200) NOT NULL DEFAULT '',
    "DocumentPrefix" varchar(200) NOT NULL DEFAULT '',
    "RequestDimensions" boolean NOT NULL DEFAULT false,
    "UseChatProfile" boolean NOT NULL DEFAULT false,
    "InputPricePerMillion" numeric(12,4) NULL CHECK ("InputPricePerMillion" >= 0),
    "OutputPricePerMillion" numeric(12,4) NULL CHECK ("OutputPricePerMillion" >= 0),
    "Fingerprint" varchar(64) NULL,
    "CreatedById" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "UQ_AiProfileRevision_Kind" UNIQUE ("Id", "Kind"),
    CONSTRAINT "CK_AiProfileRevision_Embeddings" CHECK ("Kind" <> 'embeddings'
        OR ("Provider" = 'openai-compatible' AND "Dimensions" IS NOT NULL AND "Fingerprint" IS NOT NULL)),
    CONSTRAINT "CK_AiProfileRevision_EmbeddingFields" CHECK ("Kind" = 'embeddings'
        OR ("Dimensions" IS NULL AND "Fingerprint" IS NULL)),
    CONSTRAINT "CK_AiProfileRevision_ChatProfile" CHECK ("Kind" = 'quick' OR NOT "UseChatProfile")
);

-- Revisions are history, like the access audit rows (atlas_access_audit_history_guard), but without even a closable
-- interval: a change is a new revision plus a moved assignment.
CREATE FUNCTION atlas_ai_revision_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'AI profile revisions are immutable; insert a new revision and move the assignment' USING ERRCODE = '23514';
END $$;
CREATE TRIGGER "TR_AiProfileRevision_Immutable" BEFORE UPDATE OR DELETE ON "AiProfileRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_ai_revision_immutable();

-- The current revision of each profile kind. Its ActiveFrom is the CAS version of the profile section. The composite
-- foreign key keeps the assignment's kind equal to the revision's kind.
CREATE TABLE "AiProfileAssignment" (
    "Id" uuid PRIMARY KEY,
    "Kind" varchar(16) NOT NULL CHECK ("Kind" IN ('chat', 'embeddings', 'quick')),
    "RevisionId" uuid NOT NULL,
    "ChangedById" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz,
    CONSTRAINT "FK_AiProfileAssignment_Revision" FOREIGN KEY ("RevisionId", "Kind") REFERENCES "AiProfileRevision"("Id", "Kind"),
    CONSTRAINT "CK_AiProfileAssignment_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_AiProfileAssignment_Interval" EXCLUDE USING gist
        ("Kind" WITH =, tstzrange("ActiveFrom", "ActiveTo", '[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_AiProfileAssignment_Current" ON "AiProfileAssignment" ("Kind") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_AiProfileAssignment_Revision" ON "AiProfileAssignment" ("RevisionId");

-- Switches of the AI tasks (AiTaskCatalog): { "<task>": { "enabled": bool, "cloudWorkingText": bool } }. A missing task
-- takes its catalog default. Stored as text like the other mapped JSON columns, checked to be a JSON object.
CREATE TABLE "AiFeatureSettings" (
    "Id" uuid PRIMARY KEY,
    "TasksJson" text NOT NULL CHECK (jsonb_typeof("TasksJson"::jsonb) = 'object'),
    "ChangedById" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz,
    CONSTRAINT "CK_AiFeatureSettings_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_AiFeatureSettings_Interval" EXCLUDE USING gist (tstzrange("ActiveFrom", "ActiveTo", '[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_AiFeatureSettings_Current" ON "AiFeatureSettings" ((true)) WHERE "ActiveTo" IS NULL;

-- Model usage, written in batches by A2 and read by the overview. Never content and never the user (spec RAG §2, §7):
-- task, profile revision, model, token counts, latency and an outcome code. NULL tokens mean unknown, not zero.
CREATE TABLE "AiUsage" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "OccurredAt" timestamptz NOT NULL,
    "Task" varchar(48) NOT NULL,
    "Kind" varchar(16) NOT NULL,
    "Operation" varchar(16) NOT NULL CHECK ("Operation" IN ('chat', 'embedding')),
    "ProfileRevisionId" uuid NOT NULL REFERENCES "AiProfileRevision"("Id"),
    "Model" varchar(200) NOT NULL,
    "Items" integer NULL,
    "InputTokens" bigint NULL,
    "OutputTokens" bigint NULL,
    "LatencyMs" integer NOT NULL,
    "Outcome" varchar(32) NOT NULL
);
CREATE INDEX "IX_AiUsage_OccurredAt" ON "AiUsage" ("OccurredAt");
CREATE INDEX "IX_AiUsage_ProfileRevision" ON "AiUsage" ("ProfileRevisionId");
