-- Atlas 3 Projector v1 (spec 2026-10-27, §4): requirement blocks live in page content. The tables of the projector module:
-- ProjectorCatalogPage marks the pages whose published blocks form the space's catalog (content, backed up), ProjectorCatalogEvent
-- audits who marked and unmarked them (operational); the requirement index is a derived, rebuildable projection of the blocks of every
-- page's working and published revision, and ProjectorIndexedRevision remembers which revisions it has read (both not backed up).
-- The tables and triggers exist whether the module is enabled or not, so switching it on finds the index complete.

CREATE TABLE "ProjectorCatalogPage" (
  "PageId" uuid NOT NULL CONSTRAINT "PK_ProjectorCatalogPage" PRIMARY KEY
    CONSTRAINT "FK_ProjectorCatalogPage_Page" REFERENCES "Page"("Id") ON DELETE CASCADE,
  "MarkedById" uuid NOT NULL CONSTRAINT "FK_ProjectorCatalogPage_MarkedBy" REFERENCES "UserAccount"("Id"),
  -- The catalog order: a key defined on two catalog pages takes the page marked first.
  "MarkedAt" timestamptz NOT NULL);

CREATE TABLE "ProjectorCatalogEvent" (
  "Id" bigint GENERATED ALWAYS AS IDENTITY CONSTRAINT "PK_ProjectorCatalogEvent" PRIMARY KEY,
  -- RESTRICT as PageAccessEvent: the audit trail is never erased with what it describes (review A2-4).
  "PageId" uuid NOT NULL CONSTRAINT "FK_ProjectorCatalogEvent_Page" REFERENCES "Page"("Id") ON DELETE RESTRICT,
  "ActorId" uuid NOT NULL CONSTRAINT "FK_ProjectorCatalogEvent_Actor" REFERENCES "UserAccount"("Id"),
  "IsCatalog" boolean NOT NULL,
  "Channel" varchar(16) NOT NULL,
  "ApiTokenId" uuid NULL CONSTRAINT "FK_ProjectorCatalogEvent_ApiToken" REFERENCES "PersonalApiToken"("Id"),
  "OccurredAt" timestamptz NOT NULL DEFAULT clock_timestamp());
CREATE INDEX "IX_ProjectorCatalogEvent_Page" ON "ProjectorCatalogEvent"("PageId","Id" DESC);

-- One row per requirement block of a revision that is the page's working one, its published one, or both. Values are the node's
-- attributes as stored (the editor's manifest validates them); BlockId stays text, as the client wrote it. Readers choose the
-- working or the published rows of a page on the actor's current rights.
CREATE TABLE "ProjectorRequirementIndex" (
  "PageId" uuid NOT NULL CONSTRAINT "FK_ProjectorRequirementIndex_Page" REFERENCES "Page"("Id") ON DELETE CASCADE,
  "RevisionId" uuid NOT NULL,
  -- Document order within the revision, from 1.
  "Position" integer NOT NULL,
  "IsWorking" boolean NOT NULL,
  "IsPublished" boolean NOT NULL,
  "BlockId" text NOT NULL,
  "Key" text NOT NULL,
  -- The text of the title paragraph as the projection reads it (RequirementText.Title).
  "Title" text NOT NULL,
  "Direction" text NOT NULL,
  "State" text NOT NULL,
  "Date" text NOT NULL,
  "Party" text NOT NULL,
  "Where" text NOT NULL,
  -- The comma list of keys, as stored.
  "Requires" text NOT NULL,
  "ParentBlockId" text NULL,
  CONSTRAINT "PK_ProjectorRequirementIndex" PRIMARY KEY ("PageId","RevisionId","Position"));

-- The working and published revisions the index has read, with requirement blocks or without: a save fires the refresh for the new
-- revision and the closed one, and a revision read once is not read (detoasted) again.
CREATE TABLE "ProjectorIndexedRevision" (
  "PageId" uuid NOT NULL CONSTRAINT "FK_ProjectorIndexedRevision_Page" REFERENCES "Page"("Id") ON DELETE CASCADE,
  "RevisionId" uuid NOT NULL,
  CONSTRAINT "PK_ProjectorIndexedRevision" PRIMARY KEY ("PageId","RevisionId"));

-- The single reading of the requirement node in stored rich content: every "requirement" node at any depth in document order,
-- with the nearest requirement around it. Attributes a node leaves out read as the schema's defaults; the title is the first
-- paragraph's text with a line break as a space, trimmed, as RequirementText.Title reads it in C#.
CREATE FUNCTION atlas_projector_requirements(rich text)
RETURNS TABLE("Position" integer, "BlockId" text, "Key" text, "Title" text, "Direction" text, "State" text, "Date" text, "Party" text,
  "Where" text, "Requires" text, "ParentBlockId" text)
LANGUAGE sql IMMUTABLE AS $$
  WITH RECURSIVE walk(node, parent, path) AS (
    SELECT c.value, NULL::text, ARRAY[c.ordinality]
    FROM jsonb_array_elements(CASE WHEN jsonb_typeof(rich::jsonb->'document'->'content')='array'
      THEN rich::jsonb->'document'->'content' ELSE '[]'::jsonb END) WITH ORDINALITY c
    UNION ALL
    SELECT c.value, CASE WHEN w.node->>'type'='requirement' THEN coalesce(w.node->'attrs'->>'id','') ELSE w.parent END, w.path || c.ordinality
    FROM walk w CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(w.node->'content')='array'
      THEN w.node->'content' ELSE '[]'::jsonb END) WITH ORDINALITY c
  )
  SELECT CAST(row_number() OVER (ORDER BY path) AS integer), coalesce(a->>'id',''), coalesce(a->>'key',''),
    coalesce(btrim((SELECT string_agg(CASE t.c->>'type' WHEN 'text' THEN coalesce(t.c->>'text','') WHEN 'hardBreak' THEN ' ' ELSE '' END,'' ORDER BY t.o)
      FROM jsonb_array_elements(CASE WHEN jsonb_typeof(node->'content'->0->'content')='array' THEN node->'content'->0->'content' ELSE '[]'::jsonb END)
        WITH ORDINALITY t(c,o)),' '),''),
    coalesce(a->>'direction','internal'), coalesce(a->>'state','planned'), coalesce(a->>'date',''), coalesce(a->>'party',''),
    coalesce(a->>'where',''), coalesce(a->>'requires',''), parent
  FROM (SELECT node, parent, path, node->'attrs' AS a FROM walk WHERE node->>'type'='requirement') r
$$;

-- The rows of one page, refreshed incrementally: a revision that is neither working nor published any more is forgotten with its rows,
-- the flags of the others move, and only a revision not read yet is walked (changed: the revision whose ContentJson the firing changed
-- in place, read again). A run that finds every revision read touches no content. JIT off: the planner's estimates for the JSON walk
-- cross the JIT threshold and compiling took 7-18 ms per firing, far more than the run.
CREATE FUNCTION atlas_refresh_projector_requirements(page uuid, changed uuid DEFAULT NULL) RETURNS void LANGUAGE sql SET jit = off AS $$
  DELETE FROM "ProjectorIndexedRevision" x
  WHERE x."PageId"=page AND (x."RevisionId"=changed OR NOT EXISTS (
    SELECT 1 FROM "Page" p JOIN "PageRevision" r ON r."PageId"=p."Id"
    WHERE p."Id"=page AND r."Id"=x."RevisionId" AND (r."ActiveTo" IS NULL OR r."Id"=p."PublishedRevisionId")));
  DELETE FROM "ProjectorRequirementIndex" i
  WHERE i."PageId"=page AND NOT EXISTS (SELECT 1 FROM "ProjectorIndexedRevision" x WHERE x."PageId"=page AND x."RevisionId"=i."RevisionId");
  UPDATE "ProjectorRequirementIndex" i SET "IsWorking"=r."ActiveTo" IS NULL, "IsPublished"=r."Id" IS NOT DISTINCT FROM p."PublishedRevisionId"
  FROM "Page" p JOIN "PageRevision" r ON r."PageId"=p."Id"
  WHERE p."Id"=page AND i."PageId"=page AND i."RevisionId"=r."Id"
    AND (i."IsWorking",i."IsPublished") IS DISTINCT FROM (r."ActiveTo" IS NULL,r."Id" IS NOT DISTINCT FROM p."PublishedRevisionId");
  WITH fresh AS MATERIALIZED (
    SELECT r."Id",r."ActiveTo" IS NULL AS "IsWorking",r."Id" IS NOT DISTINCT FROM p."PublishedRevisionId" AS "IsPublished",r."ContentJson"
    FROM "Page" p JOIN "PageRevision" r ON r."PageId"=p."Id" AND (r."ActiveTo" IS NULL OR r."Id"=p."PublishedRevisionId")
    WHERE p."Id"=page AND NOT EXISTS (SELECT 1 FROM "ProjectorIndexedRevision" x WHERE x."PageId"=page AND x."RevisionId"=r."Id")
  ), walked AS (
    INSERT INTO "ProjectorIndexedRevision"("PageId","RevisionId") SELECT page,"Id" FROM fresh
  )
  INSERT INTO "ProjectorRequirementIndex"("PageId","RevisionId","Position","IsWorking","IsPublished","BlockId","Key","Title","Direction",
    "State","Date","Party","Where","Requires","ParentBlockId")
  SELECT page,f."Id",x."Position",f."IsWorking",f."IsPublished",x."BlockId",x."Key",x."Title",
    x."Direction",x."State",x."Date",x."Party",x."Where",x."Requires",x."ParentBlockId"
  FROM fresh f CROSS JOIN LATERAL atlas_projector_requirements(f."ContentJson") x
  WHERE strpos(f."ContentJson",'"requirement"')>0;
$$;

-- Deferred to the commit, as PageLink's trigger: a save closes one revision and inserts the next, import and restore insert a page
-- and its revisions in either order; the refresh reads what the transaction leaves. Every content write (save, restore of a
-- revision, import, source replace, MCP) goes through these columns, so no handler has to remember the index. An update fires only
-- when a watched column really changed: NHibernate writes every column of the row it saves.
CREATE FUNCTION atlas_projector_requirements_trigger() RETURNS trigger LANGUAGE plpgsql SET jit = off AS $$
BEGIN
  IF TG_TABLE_NAME='Page' THEN
    PERFORM atlas_refresh_projector_requirements(NEW."Id");
  ELSIF TG_OP='INSERT' THEN
    PERFORM atlas_refresh_projector_requirements(NEW."PageId");
  ELSE
    -- OLD only on an update; ContentJson changed in place reads that revision again.
    PERFORM atlas_refresh_projector_requirements(NEW."PageId", CASE WHEN OLD."ContentJson" IS DISTINCT FROM NEW."ContentJson" THEN NEW."Id" END);
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER "TR_Page_ProjectorRequirementIndex" AFTER UPDATE OF "PublishedRevisionId" ON "Page"
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
  WHEN (OLD."PublishedRevisionId" IS DISTINCT FROM NEW."PublishedRevisionId") EXECUTE FUNCTION atlas_projector_requirements_trigger();
CREATE CONSTRAINT TRIGGER "TR_PageRevision_ProjectorRequirementIndex" AFTER INSERT ON "PageRevision"
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION atlas_projector_requirements_trigger();
CREATE CONSTRAINT TRIGGER "TR_PageRevision_ProjectorRequirementIndex_Update" AFTER UPDATE OF "ActiveTo","ContentJson" ON "PageRevision"
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
  WHEN (OLD."ActiveTo" IS DISTINCT FROM NEW."ActiveTo" OR OLD."ContentJson" IS DISTINCT FROM NEW."ContentJson")
  EXECUTE FUNCTION atlas_projector_requirements_trigger();

-- Backfill and the rebuild after a restore (SELECT atlas_rebuild_projector_requirements()). DELETE, not TRUNCATE, as for PageLink.
CREATE FUNCTION atlas_rebuild_projector_requirements() RETURNS void LANGUAGE sql SET jit = off AS $$
  DELETE FROM "ProjectorRequirementIndex";
  DELETE FROM "ProjectorIndexedRevision";
  SELECT count(atlas_refresh_projector_requirements(p."Id")) FROM "Page" p;
$$;
SELECT atlas_rebuild_projector_requirements();
