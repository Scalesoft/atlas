-- Atlas 2.0 V2: the knowledge index of published content (plan phase 2, §10; spec RAG §2, §6). A derived, rebuildable
-- projection like SearchDocument and PageLink: never backed up, recomputed from PageRevision.
--
-- One generation = one embeddings profile revision (fingerprint) and one chunking version. At most one Active generation
-- (what search, related pages and suggestions read) and one Building beside it (a rebuild or a model exchange); Retired
-- generations stay 24 hours (O12), Failed ones until maintenance removes them. Without any generation nothing below writes
-- a row, so a wiki without AI pays nothing (AI-24).
CREATE TABLE "IndexGeneration" (
  "Id" uuid PRIMARY KEY,
  "State" varchar(16) NOT NULL CONSTRAINT "CK_IndexGeneration_State" CHECK ("State" IN ('Building','Active','Retired','Failed')),
  "ProfileRevisionId" uuid NOT NULL CONSTRAINT "FK_IndexGeneration_Profile" REFERENCES "AiProfileRevision"("Id"),
  "Fingerprint" varchar(64) NOT NULL,
  "ChunkingVersion" varchar(32) NOT NULL,
  -- Phase 2 stores vector(1024) only (O3); another size is a new migration and a new generation.
  "Dimensions" smallint NOT NULL CONSTRAINT "CK_IndexGeneration_Dimensions" CHECK ("Dimensions" = 1024),
  "CreatedById" uuid NOT NULL CONSTRAINT "FK_IndexGeneration_Creator" REFERENCES "UserAccount"("Id"),
  "CreatedAt" timestamptz NOT NULL,
  -- Completeness boundary: the generation is complete when no task enqueued at or before it is left and every eligible page
  -- has its row. Later publications re-enqueue their pages with a later EnqueuedAt and are caught up after activation.
  "BoundaryAt" timestamptz NOT NULL,
  "ActivatedById" uuid NULL CONSTRAINT "FK_IndexGeneration_Activator" REFERENCES "UserAccount"("Id"),
  "ActivatedAt" timestamptz NULL,
  "EndedAt" timestamptz NULL,
  -- Compare-and-swap token of the admin actions (start, activate, cancel); indexing progress does not change it.
  "Version" timestamptz NOT NULL);
CREATE UNIQUE INDEX "UX_IndexGeneration_Active" ON "IndexGeneration"((true)) WHERE "State" = 'Active';
CREATE UNIQUE INDEX "UX_IndexGeneration_Building" ON "IndexGeneration"((true)) WHERE "State" = 'Building';

-- A page in a generation: the published revision it was built from, the title vector (P1–P3, E2; a template has only this,
-- O19) and the centroid of its chunks (R1, E4). The revision belongs to the page (spec RAG §2); deleting the revision or the
-- page deletes the row. TitleHash lets an unchanged title keep its vector.
CREATE TABLE "KnowledgePage" (
  "GenerationId" uuid NOT NULL CONSTRAINT "FK_KnowledgePage_Generation" REFERENCES "IndexGeneration"("Id") ON DELETE CASCADE,
  "PageId" uuid NOT NULL,
  "RevisionId" uuid NOT NULL,
  "IsTemplate" boolean NOT NULL,
  "TitleEmbedding" vector(1024) NOT NULL,
  "TitleHash" bytea NOT NULL CONSTRAINT "CK_KnowledgePage_TitleHash" CHECK (octet_length("TitleHash") = 32),
  "Centroid" vector(1024) NULL,
  "IndexedAt" timestamptz NOT NULL,
  CONSTRAINT "PK_KnowledgePage" PRIMARY KEY ("GenerationId","PageId"),
  CONSTRAINT "FK_KnowledgePage_Revision" FOREIGN KEY ("PageId","RevisionId")
    REFERENCES "PageRevision"("PageId","Id") ON DELETE CASCADE);
CREATE INDEX "IX_KnowledgePage_Revision" ON "KnowledgePage"("PageId","RevisionId");

-- A section of a published revision. Text = "{title} › {heading}\n{body}" (the first line is the context the chunk was
-- embedded with, without the profile's document prefix); TextHash = SHA-256 of the exact embedded text, prefix included,
-- so an unchanged section keeps its vector. HeadingId is the anchor of SearchHit.HeadingId (atlas_search_headings).
CREATE TABLE "KnowledgeChunk" (
  "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  "GenerationId" uuid NOT NULL,
  "PageId" uuid NOT NULL,
  "RevisionId" uuid NOT NULL,
  "Ordinal" smallint NOT NULL,
  "HeadingId" varchar(128) NULL,
  "HeadingTitle" varchar(500) NULL,
  "Text" text NOT NULL CONSTRAINT "CK_KnowledgeChunk_Text" CHECK (length("Text") <= 2600),
  "TextHash" bytea NOT NULL CONSTRAINT "CK_KnowledgeChunk_TextHash" CHECK (octet_length("TextHash") = 32),
  "Embedding" vector(1024) NOT NULL,
  CONSTRAINT "FK_KnowledgeChunk_Page" FOREIGN KEY ("GenerationId","PageId")
    REFERENCES "KnowledgePage"("GenerationId","PageId") ON DELETE CASCADE,
  CONSTRAINT "UX_KnowledgeChunk_Order" UNIQUE ("GenerationId","PageId","Ordinal"));
CREATE INDEX "IX_KnowledgeChunk_Embedding" ON "KnowledgeChunk" USING hnsw ("Embedding" vector_cosine_ops) WITH (m = 16, ef_construction = 64);
CREATE INDEX "IX_KnowledgePage_Title" ON "KnowledgePage" USING hnsw ("TitleEmbedding" vector_cosine_ops);
CREATE INDEX "IX_KnowledgePage_Centroid" ON "KnowledgePage" USING hnsw ("Centroid" vector_cosine_ops);

-- "Synchronize page P in generation G" (decision R1 / O6). No revision and no content: the worker reads the page's current
-- state, so ten publications during an outage are one task and a stale task can never index old content. EnqueuedAt
-- (clock_timestamp) is the token the worker's write compares; NextAttemptAt, Attempts and LastErrorCode are the backoff;
-- LeasedUntil the claim (FOR UPDATE SKIP LOCKED, safe for several instances).
CREATE TABLE "IndexTask" (
  "GenerationId" uuid NOT NULL CONSTRAINT "FK_IndexTask_Generation" REFERENCES "IndexGeneration"("Id") ON DELETE CASCADE,
  "PageId" uuid NOT NULL CONSTRAINT "FK_IndexTask_Page" REFERENCES "Page"("Id") ON DELETE CASCADE,
  "EnqueuedAt" timestamptz NOT NULL,
  "NextAttemptAt" timestamptz NOT NULL,
  "Attempts" int NOT NULL DEFAULT 0,
  "LeasedUntil" timestamptz NULL,
  "LastErrorCode" varchar(32) NULL,
  CONSTRAINT "PK_IndexTask" PRIMARY KEY ("GenerationId","PageId"));
CREATE INDEX "IX_IndexTask_Due" ON "IndexTask"("NextAttemptAt");
CREATE INDEX "IX_IndexTask_Page" ON "IndexTask"("PageId");

-- The one definition of "the page belongs in the index": published and not in the trash (this function), in a space
-- included in AI (atlas_space_in_ai, V6). Templates belong too, with their title only (O19). Set-based queries evaluate
-- the space part once per space (plan §14 note: the function is not inlined), single rows use atlas_ai_index_eligible.
CREATE FUNCTION atlas_ai_index_candidate(p "Page") RETURNS boolean LANGUAGE sql IMMUTABLE AS $$
  SELECT p."PublishedRevisionId" IS NOT NULL AND NOT p."IsTrashed" $$;
CREATE FUNCTION atlas_ai_index_eligible(p "Page") RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT atlas_ai_index_candidate(p) AND atlas_space_in_ai(p."SpaceId") $$;

-- One task per live generation; a repeated change of the page restarts its backoff. Nothing without a live generation, and
-- nothing for a page that no longer exists (an import or restore may delete a page it inserted in the same transaction).
CREATE FUNCTION atlas_enqueue_index_sync(page uuid) RETURNS void LANGUAGE sql AS $$
  INSERT INTO "IndexTask"("GenerationId","PageId","EnqueuedAt","NextAttemptAt")
  SELECT g."Id", page, clock_timestamp(), now() FROM "IndexGeneration" g
  WHERE g."State" IN ('Building','Active') AND EXISTS (SELECT 1 FROM "Page" p WHERE p."Id" = page)
  ON CONFLICT ("GenerationId","PageId") DO UPDATE SET "EnqueuedAt" = EXCLUDED."EnqueuedAt",
    "NextAttemptAt" = EXCLUDED."NextAttemptAt", "Attempts" = 0, "LastErrorCode" = NULL $$;

-- Deferred like TR_Page_PageLink: it runs at commit, when a revision inserted after its page exists too, and covers
-- publication, draft approval, import, backup restore, trash, moves between spaces and templates. Page has DynamicUpdate,
-- so saving a draft (Version, no listed column) never fires it. A page that was and stays unpublished is skipped.
CREATE FUNCTION atlas_index_task_trigger() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' AND NEW."PublishedRevisionId" IS NULL THEN
    RETURN NULL;
  END IF;
  IF TG_OP = 'UPDATE' AND (
      (OLD."PublishedRevisionId" IS NULL AND NEW."PublishedRevisionId" IS NULL)
      OR (OLD."PublishedRevisionId" IS NOT DISTINCT FROM NEW."PublishedRevisionId" AND OLD."SpaceId" = NEW."SpaceId"
        AND OLD."IsTrashed" = NEW."IsTrashed" AND OLD."IsTemplate" = NEW."IsTemplate")) THEN
    RETURN NULL;
  END IF;
  PERFORM atlas_enqueue_index_sync(NEW."Id");
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER "TR_Page_IndexTask"
  AFTER INSERT OR UPDATE OF "PublishedRevisionId","SpaceId","IsTrashed","IsTemplate" ON "Page"
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION atlas_index_task_trigger();

-- G5 (V6): a changed inclusion of a space enqueues its published pages, so an excluded space leaves the index and an
-- included one enters it. Reads filter by atlas_space_in_ai at once; the tasks are the follow-up clean-up or build.
CREATE FUNCTION atlas_space_ai_index_trigger() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO "IndexTask"("GenerationId","PageId","EnqueuedAt","NextAttemptAt")
  SELECT g."Id", p."Id", clock_timestamp(), now()
  FROM "IndexGeneration" g CROSS JOIN "Page" p
  WHERE g."State" IN ('Building','Active') AND p."SpaceId" = NEW."SpaceId" AND p."PublishedRevisionId" IS NOT NULL
  ON CONFLICT ("GenerationId","PageId") DO UPDATE SET "EnqueuedAt" = EXCLUDED."EnqueuedAt",
    "NextAttemptAt" = EXCLUDED."NextAttemptAt", "Attempts" = 0, "LastErrorCode" = NULL;
  RETURN NULL;
END $$;
CREATE TRIGGER "TR_SpaceAiSetting_IndexTask" AFTER INSERT ON "SpaceAiSetting"
  FOR EACH ROW EXECUTE FUNCTION atlas_space_ai_index_trigger();

-- No backfill: an upgrade has no generation yet, so nothing is enqueued until an administrator builds the first index.
