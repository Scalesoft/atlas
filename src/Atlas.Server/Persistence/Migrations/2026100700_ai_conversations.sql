-- Atlas 2.0 phase 3 Q1: private conversations, their messages and the source dependencies of every answer (plan phase 3 R4,
-- §5; spec RAG §2, §5, §7). Private user data: part of the database backup, never of the wiki ZIP backup or the content export.
--
-- What is stored is only what the flow needs: the question (≤ 2 000 characters), the validated answer of an "answered" message
-- (jsonb sentences with source numbers) and references to the sources. Never a source text, prompt or model output that did
-- not pass the gate: a withheld, failed, canceled or unavailable answer has no "Answer" (CK_AiMessage_Role).
--
-- No backfill: an upgrade gets empty tables.

-- A conversation has one owner and nobody else reads it, not even a WikiAdmin (spec RAG §2). Origin "search" is the answer
-- card of the omnisearch (Listed = false until Pokračovat v asistentovi), "assistant" the assistant panel. The pinned chat
-- profile revision is the one the conversation runs on; ChatCompatibility is the fingerprint of what decides whether it may
-- continue on a later revision (R5: provider, location, URL origin and model; ChatProfileCompatibility in C#). Version
-- counts turns (optimistic check of concurrent questions besides the FOR UPDATE of a turn).
CREATE TABLE "AiConversation" (
  "Id" uuid PRIMARY KEY,
  "OwnerId" uuid NOT NULL CONSTRAINT "FK_AiConversation_Owner" REFERENCES "UserAccount"("Id"),
  "Origin" varchar(16) NOT NULL CONSTRAINT "CK_AiConversation_Origin" CHECK ("Origin" IN ('search','assistant')),
  "Listed" boolean NOT NULL,
  "ChatProfileRevisionId" uuid NOT NULL CONSTRAINT "FK_AiConversation_Profile" REFERENCES "AiProfileRevision"("Id"),
  "ChatCompatibility" varchar(64) NOT NULL CONSTRAINT "CK_AiConversation_Compatibility" CHECK (length("ChatCompatibility") = 64),
  "CreatedAt" timestamptz NOT NULL,
  "LastActivityAt" timestamptz NOT NULL,
  "Version" integer NOT NULL CONSTRAINT "CK_AiConversation_Version" CHECK ("Version" >= 0),
  CONSTRAINT "CK_AiConversation_Activity" CHECK ("LastActivityAt" >= "CreatedAt"));
-- "Moje konverzace" (owner, listed, newest first) and the retention (unlisted after 24 h, listed after 180 days).
CREATE INDEX "IX_AiConversation_Owner" ON "AiConversation"("OwnerId","Listed","LastActivityAt" DESC);
CREATE INDEX "IX_AiConversation_Retention" ON "AiConversation"("Listed","LastActivityAt");

-- One message of a conversation in order: a question (user) followed by its answer (assistant). An answer is "preparing"
-- between the two steps of R1 and ends in exactly one terminal state; only "answered" carries the answer.
CREATE TABLE "AiMessage" (
  "Id" uuid PRIMARY KEY,
  "ConversationId" uuid NOT NULL CONSTRAINT "FK_AiMessage_Conversation" REFERENCES "AiConversation"("Id") ON DELETE CASCADE,
  "Ordinal" smallint NOT NULL CONSTRAINT "CK_AiMessage_Ordinal" CHECK ("Ordinal" >= 1),
  "Role" varchar(16) NOT NULL CONSTRAINT "CK_AiMessage_RoleValue" CHECK ("Role" IN ('user','assistant')),
  "State" varchar(16) NULL CONSTRAINT "CK_AiMessage_State"
    CHECK ("State" IN ('preparing','answered','no_sources','withheld','failed','canceled','unavailable')),
  "Task" varchar(48) NULL,                                   -- search.answer | chat.answer
  "Question" text NULL CONSTRAINT "CK_AiMessage_Question" CHECK (length("Question") BETWEEN 1 AND 2000),
  "ScopeKind" varchar(8) NULL CONSTRAINT "CK_AiMessage_ScopeKind" CHECK ("ScopeKind" IN ('wiki','space','page')),
  "ScopeSpaceId" uuid NULL CONSTRAINT "FK_AiMessage_ScopeSpace" REFERENCES "Space"("Id") ON DELETE SET NULL,
  "ScopePageId" uuid NULL CONSTRAINT "FK_AiMessage_ScopePage" REFERENCES "Page"("Id") ON DELETE SET NULL,
  "Answer" jsonb NULL CONSTRAINT "CK_AiMessage_Answer" CHECK (jsonb_typeof("Answer") = 'object'),
  "PromptVersion" varchar(32) NULL,
  "ProfileRevisionId" uuid NULL CONSTRAINT "FK_AiMessage_Profile" REFERENCES "AiProfileRevision"("Id"),
  "Model" varchar(200) NULL,
  "InputTokens" integer NULL CONSTRAINT "CK_AiMessage_InputTokens" CHECK ("InputTokens" >= 0),
  "OutputTokens" integer NULL CONSTRAINT "CK_AiMessage_OutputTokens" CHECK ("OutputTokens" >= 0),
  "LatencyMs" integer NULL CONSTRAINT "CK_AiMessage_Latency" CHECK ("LatencyMs" >= 0),
  "Retrieval" varchar(8) NULL CONSTRAINT "CK_AiMessage_Retrieval" CHECK ("Retrieval" IN ('hybrid','lexical')),
  "InvalidCitations" smallint NULL CONSTRAINT "CK_AiMessage_InvalidCitations" CHECK ("InvalidCitations" >= 0),
  "DroppedSentences" smallint NULL CONSTRAINT "CK_AiMessage_DroppedSentences" CHECK ("DroppedSentences" >= 0),
  "ErrorCode" varchar(32) NULL,
  "CreatedAt" timestamptz NOT NULL,
  "CompletedAt" timestamptz NULL,
  CONSTRAINT "UX_AiMessage_Order" UNIQUE ("ConversationId","Ordinal"),
  CONSTRAINT "CK_AiMessage_Role" CHECK (
    ("Role" = 'user' AND "Question" IS NOT NULL AND "ScopeKind" IS NOT NULL AND "State" IS NULL AND "Answer" IS NULL
       AND "Task" IS NULL AND "CompletedAt" IS NULL)
    OR ("Role" = 'assistant' AND "Question" IS NULL AND "ScopeKind" IS NULL AND "State" IS NOT NULL AND "Task" IS NOT NULL
       AND ("Answer" IS NULL OR "State" = 'answered')
       -- An answer is complete exactly when it left "preparing".
       AND (("State" = 'preparing') = ("CompletedAt" IS NULL)))),
  -- A wiki-wide question names no space or page; a deleted space or page leaves its scope empty (SET NULL).
  CONSTRAINT "CK_AiMessage_Scope" CHECK ("ScopeKind" IS DISTINCT FROM 'wiki' OR ("ScopeSpaceId" IS NULL AND "ScopePageId" IS NULL)));
-- Turns left "preparing" by a crashed client or a restart (canceled after 10 minutes), and the daily limit.
CREATE INDEX "IX_AiMessage_Preparing" ON "AiMessage"("CreatedAt") WHERE "State" = 'preparing';
CREATE INDEX "IX_AiMessage_Daily" ON "AiMessage"("ConversationId","CreatedAt") WHERE "Role" = 'assistant';
CREATE INDEX "IX_AiMessage_ScopeSpace" ON "AiMessage"("ScopeSpaceId") WHERE "ScopeSpaceId" IS NOT NULL;
CREATE INDEX "IX_AiMessage_ScopePage" ON "AiMessage"("ScopePageId") WHERE "ScopePageId" IS NOT NULL;

-- Every source of an answer's prompt, cited or not (spec RAG §5 point 8): Kind "context" are the sources [1..n] of the prompt
-- (Ordinal = n), Kind "history" the dependencies of earlier answers whose sentences the prompt repeats, copied so the closure
-- is complete without recursion (R4). SourceType and SourceVersion name the adapter (AI-11). A page keeps its real FK to the
-- revision (spec RAG §1: never weakened for future types); every other type has ExternalKey/ExternalRevision only. The chunk
-- locator (GenerationId, ChunkOrdinal) has no FK: generations are retired and removed, the text is then simply gone.
CREATE TABLE "AiSourceDependency" (
  "MessageId" uuid NOT NULL CONSTRAINT "FK_AiSourceDependency_Message" REFERENCES "AiMessage"("Id") ON DELETE CASCADE,
  "Ordinal" smallint NOT NULL CONSTRAINT "CK_AiSourceDependency_Ordinal" CHECK ("Ordinal" >= 1),
  "Kind" varchar(8) NOT NULL CONSTRAINT "CK_AiSourceDependency_Kind" CHECK ("Kind" IN ('context','history')),
  "SourceType" varchar(32) NOT NULL,
  "SourceVersion" smallint NOT NULL CONSTRAINT "CK_AiSourceDependency_Version" CHECK ("SourceVersion" >= 1),
  "PageId" uuid NULL,
  "RevisionId" uuid NULL,
  "ExternalKey" varchar(200) NULL,
  "ExternalRevision" varchar(64) NULL,
  "GenerationId" uuid NULL,
  "ChunkOrdinal" smallint NULL,
  "HeadingId" varchar(128) NULL,
  "Cited" boolean NOT NULL DEFAULT false,
  CONSTRAINT "PK_AiSourceDependency" PRIMARY KEY ("MessageId","Ordinal"),
  -- Permanent FK (spec RAG §1): a deleted page or revision nulls both columns and the message becomes unavailable.
  CONSTRAINT "FK_AiSourceDependency_Revision" FOREIGN KEY ("PageId","RevisionId")
    REFERENCES "PageRevision"("PageId","Id") ON DELETE SET NULL,
  CONSTRAINT "CK_AiSourceDependency_Type" CHECK (
    ("SourceType" = 'page' AND "ExternalKey" IS NULL AND "ExternalRevision" IS NULL)
    OR ("SourceType" <> 'page' AND "PageId" IS NULL AND "RevisionId" IS NULL AND "ExternalKey" IS NOT NULL)),
  CONSTRAINT "CK_AiSourceDependency_PageRevision" CHECK (("PageId" IS NULL) = ("RevisionId" IS NULL)),
  CONSTRAINT "CK_AiSourceDependency_Chunk" CHECK (("GenerationId" IS NULL) = ("ChunkOrdinal" IS NULL)),
  CONSTRAINT "CK_AiSourceDependency_Cited" CHECK (NOT "Cited" OR "Kind" = 'context'));
-- Serves the SET NULL of a deleted revision and "which answers used this page".
CREATE INDEX "IX_AiSourceDependency_Page" ON "AiSourceDependency"("PageId","RevisionId") WHERE "PageId" IS NOT NULL;

-- The status of the dependencies of the given messages for reading history (Q4) and continuing a conversation (Q3), in one
-- batch. The rules are those of retrieval (plan phase 3 §3): the actor active with its current stamp, the page exists, is not
-- in the trash, is published, is not a template, is readable (EffectivePageAccess role >= 1 or WikiAdmin) and lies in a space
-- included in AI (atlas_space_in_ai once per space). changed = the published revision differs from the dependency's.
-- Only messages of the actor's own conversations are evaluated: a dependency of anybody else's message is never available.
-- Dependencies of other source types (or another version of the page adapter) come back with available NULL; their adapter
-- decides, and an unknown one refuses.
CREATE FUNCTION atlas_ai_dependency_status(p_actor uuid, p_stamp text, p_messages uuid[])
RETURNS TABLE(message_id uuid, ordinal smallint, kind text, available boolean, changed boolean)
LANGUAGE sql STABLE AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin"
          FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), dependency AS MATERIALIZED (
          SELECT d."MessageId",d."Ordinal",d."Kind",d."PageId",d."RevisionId",
            (d."SourceType"='page' AND d."SourceVersion"=1) AS is_page,
            (c."OwnerId"=p_actor) AS owned
          FROM "AiSourceDependency" d
          JOIN "AiMessage" m ON m."Id"=d."MessageId"
          JOIN "AiConversation" c ON c."Id"=m."ConversationId"
          WHERE d."MessageId" = ANY (p_messages)
        ), page AS MATERIALIZED (
          SELECT p."Id",p."SpaceId",p."PublishedRevisionId",p."IsTrashed",p."IsTemplate" FROM "Page" p
          WHERE p."Id" IN (SELECT "PageId" FROM dependency WHERE is_page AND owned AND "PageId" IS NOT NULL)
        ), included AS MATERIALIZED (
          SELECT s."SpaceId" FROM (SELECT DISTINCT "SpaceId" FROM page) s WHERE atlas_space_in_ai(s."SpaceId")
        ), access AS MATERIALIZED (
          SELECT e."PageId" FROM "EffectivePageAccess" e
          WHERE e."UserId"=p_actor AND e."Role">=1 AND e."PageId" IN (SELECT "Id" FROM page)
        ), status AS (
          SELECT d."MessageId",d."Ordinal",d."Kind",d.is_page,d.owned,d."RevisionId",p."PublishedRevisionId",
            COALESCE(EXISTS (SELECT 1 FROM actor)
              AND p."Id" IS NOT NULL AND p."PublishedRevisionId" IS NOT NULL AND NOT p."IsTrashed" AND NOT p."IsTemplate"
              AND p."SpaceId" IN (SELECT "SpaceId" FROM included)
              AND ((SELECT a."IsWikiAdmin" FROM actor a) OR p."Id" IN (SELECT "PageId" FROM access)), false) AS readable
          FROM dependency d LEFT JOIN page p ON p."Id"=d."PageId"
        )
        SELECT s."MessageId",s."Ordinal",s."Kind"::text,
          CASE WHEN NOT s.owned THEN false WHEN s.is_page THEN s.readable END,
          CASE WHEN NOT s.owned THEN false WHEN s.is_page THEN s.readable AND s."PublishedRevisionId"<>s."RevisionId" END
        FROM status s
        ORDER BY s."MessageId",s."Ordinal"
$$;
