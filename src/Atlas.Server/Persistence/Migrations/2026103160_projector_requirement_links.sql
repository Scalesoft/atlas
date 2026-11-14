-- Atlas 3.1 M4b (plan §2.3): a requirement's party may be a contact (the node's partyContactId, the index's PartyContactId, a use of
-- the contact), and every block keeps who changed it when (ProjectorRequirementChange): the index's projection of a block compared
-- between revisions, a 'save' row when the working revision's differs from the block's last saved one (its revision's author and time),
-- a 'publish' row when the publication's differs from the last published one (who made that version, and who published it when).
-- The history is derived from the revisions, as the index: rebuilt with it after a restore, never backed up.

ALTER TABLE "ProjectorRequirementIndex"
  ADD "PartyContactId" text NOT NULL DEFAULT '',
  -- What the history compares (atlas_projector_fingerprint): the attributes and the title, not the position.
  ADD "Fingerprint" text NOT NULL DEFAULT '';
-- The usage of a contact as a party (ProjectorPartyRelations) asks by contact.
CREATE INDEX "IX_ProjectorRequirementIndex_PartyContact" ON "ProjectorRequirementIndex"("PartyContactId") WHERE "PartyContactId"<>'';

CREATE TABLE "ProjectorRequirementChange" (
  "Id" bigint GENERATED ALWAYS AS IDENTITY CONSTRAINT "PK_ProjectorRequirementChange" PRIMARY KEY,
  "PageId" uuid NOT NULL CONSTRAINT "FK_ProjectorRequirementChange_Page" REFERENCES "Page"("Id") ON DELETE CASCADE,
  "BlockId" text NOT NULL,
  "RevisionId" uuid NOT NULL,
  -- 'save' (the working revision) or 'publish' (the publication).
  "Kind" text NOT NULL CONSTRAINT "CK_ProjectorRequirementChange_Kind" CHECK ("Kind" IN ('save','publish')),
  "Fingerprint" text NOT NULL,
  -- Who made this version of the block and when: the revision's author, for a publication the save that made it.
  "ChangedById" uuid NOT NULL,
  "ChangedAt" timestamptz NOT NULL,
  "PublishedById" uuid NULL,
  "PublishedAt" timestamptz NULL,
  "Title" text NOT NULL,
  "State" text NOT NULL);
CREATE INDEX "IX_ProjectorRequirementChange_Block" ON "ProjectorRequirementChange"("PageId","BlockId","Kind",(coalesce("PublishedAt","ChangedAt")) DESC,"Id" DESC);

CREATE FUNCTION atlas_projector_fingerprint(key text, title text, direction text, state text, date text, party text, party_contact text,
  "where" text, requires text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
  SELECT md5(concat_ws(chr(31), key, title, direction, state, date, party, party_contact, "where", requires))
$$;

-- The reading of 2026102800 with partyContactId (the schema's default "" when left out). A new column of the result: dropped and created.
DROP FUNCTION atlas_projector_requirements(text);
CREATE FUNCTION atlas_projector_requirements(rich text)
RETURNS TABLE("Position" integer, "BlockId" text, "Key" text, "Title" text, "Direction" text, "State" text, "Date" text, "Party" text,
  "Where" text, "Requires" text, "ParentBlockId" text, "PartyContactId" text)
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
    coalesce(a->>'where',''), coalesce(a->>'requires',''), parent, coalesce(a->>'partyContactId','')
  FROM (SELECT node, parent, path, node->'attrs' AS a FROM walk WHERE node->>'type'='requirement') r
$$;

-- 2026103011's refresh with the new columns; the working revision just read is compared with each block's last 'save' row.
CREATE OR REPLACE FUNCTION atlas_refresh_projector_requirements(page uuid, changed uuid DEFAULT NULL) RETURNS void LANGUAGE sql SET jit = off AS $$
  DELETE FROM "ProjectorIndexedRevision" x
  WHERE x."PageId"=page AND (x."RevisionId"=changed OR NOT EXISTS (
    SELECT 1 FROM "Page" p JOIN "PageRevision" r ON r."PageId"=p."Id"
    WHERE p."Id"=page AND p."PurgedAt" IS NULL AND r."Id"=x."RevisionId" AND (r."ActiveTo" IS NULL OR r."Id"=p."PublishedRevisionId")));
  DELETE FROM "ProjectorRequirementIndex" i
  WHERE i."PageId"=page AND NOT EXISTS (SELECT 1 FROM "ProjectorIndexedRevision" x WHERE x."PageId"=page AND x."RevisionId"=i."RevisionId");
  UPDATE "ProjectorRequirementIndex" i SET "IsWorking"=r."ActiveTo" IS NULL, "IsPublished"=r."Id" IS NOT DISTINCT FROM p."PublishedRevisionId"
  FROM "Page" p JOIN "PageRevision" r ON r."PageId"=p."Id"
  WHERE p."Id"=page AND i."PageId"=page AND i."RevisionId"=r."Id"
    AND (i."IsWorking",i."IsPublished") IS DISTINCT FROM (r."ActiveTo" IS NULL,r."Id" IS NOT DISTINCT FROM p."PublishedRevisionId");
  WITH fresh AS MATERIALIZED (
    SELECT r."Id",r."ActiveTo" IS NULL AS "IsWorking",r."Id" IS NOT DISTINCT FROM p."PublishedRevisionId" AS "IsPublished",r."ContentJson",
      r."AuthorId",r."ActiveFrom"
    FROM "Page" p JOIN "PageRevision" r ON r."PageId"=p."Id" AND (r."ActiveTo" IS NULL OR r."Id"=p."PublishedRevisionId")
    WHERE p."Id"=page AND p."PurgedAt" IS NULL
      AND NOT EXISTS (SELECT 1 FROM "ProjectorIndexedRevision" x WHERE x."PageId"=page AND x."RevisionId"=r."Id")
  ), walked AS (
    INSERT INTO "ProjectorIndexedRevision"("PageId","RevisionId") SELECT page,"Id" FROM fresh
  ), blocks AS MATERIALIZED (
    SELECT f."Id" AS "RevisionId",f."IsWorking",f."IsPublished",f."AuthorId",f."ActiveFrom",x.*,
      atlas_projector_fingerprint(x."Key",x."Title",x."Direction",x."State",x."Date",x."Party",x."PartyContactId",x."Where",x."Requires") AS "Fingerprint"
    FROM fresh f CROSS JOIN LATERAL atlas_projector_requirements(f."ContentJson") x
    WHERE strpos(f."ContentJson",'"requirement"')>0
  ), saved AS (
    INSERT INTO "ProjectorRequirementChange"("PageId","BlockId","RevisionId","Kind","Fingerprint","ChangedById","ChangedAt","Title","State")
    SELECT page,b."BlockId",b."RevisionId",'save',b."Fingerprint",b."AuthorId",b."ActiveFrom",b."Title",b."State" FROM blocks b
    WHERE b."IsWorking" AND b."BlockId"<>'' AND b."Fingerprint" IS DISTINCT FROM (
      SELECT c."Fingerprint" FROM "ProjectorRequirementChange" c WHERE c."PageId"=page AND c."BlockId"=b."BlockId" AND c."Kind"='save'
      ORDER BY coalesce(c."PublishedAt",c."ChangedAt") DESC,c."Id" DESC LIMIT 1)
  )
  INSERT INTO "ProjectorRequirementIndex"("PageId","RevisionId","Position","IsWorking","IsPublished","BlockId","Key","Title","Direction",
    "State","Date","Party","Where","Requires","ParentBlockId","PartyContactId","Fingerprint")
  SELECT page,b."RevisionId",b."Position",b."IsWorking",b."IsPublished",b."BlockId",b."Key",b."Title",
    b."Direction",b."State",b."Date",b."Party",b."Where",b."Requires",b."ParentBlockId",b."PartyContactId",b."Fingerprint"
  FROM blocks b;
$$;

-- A publication: each block of the published revision whose projection differs from its last 'publish' row gets one, with the save
-- that made that version (its author and time, or the revision's) and the current publication's publisher and time.
CREATE FUNCTION atlas_record_projector_publication(page uuid) RETURNS void LANGUAGE sql SET jit = off AS $$
  INSERT INTO "ProjectorRequirementChange"("PageId","BlockId","RevisionId","Kind","Fingerprint","ChangedById","ChangedAt","PublishedById","PublishedAt",
    "Title","State")
  SELECT page,i."BlockId",i."RevisionId",'publish',i."Fingerprint",coalesce(s."ChangedById",r."AuthorId"),coalesce(s."ChangedAt",r."ActiveFrom"),
    coalesce(u."PublisherId",r."AuthorId"),coalesce(u."ActiveFrom",r."ActiveFrom"),i."Title",i."State"
  FROM "ProjectorRequirementIndex" i JOIN "PageRevision" r ON r."Id"=i."RevisionId"
  LEFT JOIN "PagePublication" u ON u."PageId"=page AND u."RevisionId"=i."RevisionId" AND u."ActiveTo" IS NULL
  LEFT JOIN LATERAL (
    SELECT c."ChangedById",c."ChangedAt" FROM "ProjectorRequirementChange" c
    WHERE c."PageId"=page AND c."BlockId"=i."BlockId" AND c."Kind"='save' AND c."Fingerprint"=i."Fingerprint" AND c."ChangedAt"<=r."ActiveFrom"
    ORDER BY c."ChangedAt" DESC,c."Id" DESC LIMIT 1) s ON true
  WHERE i."PageId"=page AND i."IsPublished" AND i."BlockId"<>'' AND i."Fingerprint" IS DISTINCT FROM (
    SELECT c."Fingerprint" FROM "ProjectorRequirementChange" c WHERE c."PageId"=page AND c."BlockId"=i."BlockId" AND c."Kind"='publish'
    ORDER BY coalesce(c."PublishedAt",c."ChangedAt") DESC,c."Id" DESC LIMIT 1);
$$;

CREATE OR REPLACE FUNCTION atlas_projector_requirements_trigger() RETURNS trigger LANGUAGE plpgsql SET jit = off AS $$
BEGIN
  IF TG_TABLE_NAME='Page' THEN
    PERFORM atlas_refresh_projector_requirements(NEW."Id");
    PERFORM atlas_record_projector_publication(NEW."Id");
  ELSIF TG_OP='INSERT' THEN
    PERFORM atlas_refresh_projector_requirements(NEW."PageId");
  ELSE
    -- OLD only on an update; ContentJson changed in place reads that revision again.
    PERFORM atlas_refresh_projector_requirements(NEW."PageId", CASE WHEN OLD."ContentJson" IS DISTINCT FROM NEW."ContentJson" THEN NEW."Id" END);
  END IF;
  RETURN NULL;
END $$;

-- The history of pages whose working or published revision holds requirements and that have none yet, read from every revision of
-- theirs in version order: a 'save' row where a block's projection differs from its previous revision's, a 'publish' row where a
-- publication's differs from the previous publication's. One run per page: an existing page's history is kept.
CREATE FUNCTION atlas_backfill_projector_history() RETURNS void LANGUAGE sql SET jit = off AS $$
  WITH pages AS MATERIALIZED (
    SELECT DISTINCT p."Id" FROM "Page" p JOIN "PageRevision" r ON r."PageId"=p."Id" AND (r."ActiveTo" IS NULL OR r."Id"=p."PublishedRevisionId")
    WHERE p."PurgedAt" IS NULL AND strpos(r."ContentJson",'"requirement"')>0
      AND NOT EXISTS (SELECT 1 FROM "ProjectorRequirementChange" c WHERE c."PageId"=p."Id")
  ), blocks AS MATERIALIZED (
    SELECT r."PageId",r."Id" AS "RevisionId",r."Version",r."AuthorId",r."ActiveFrom",x."BlockId",x."Title",x."State",
      atlas_projector_fingerprint(x."Key",x."Title",x."Direction",x."State",x."Date",x."Party",x."PartyContactId",x."Where",x."Requires") AS "Fingerprint"
    FROM pages g JOIN "PageRevision" r ON r."PageId"=g."Id" CROSS JOIN LATERAL atlas_projector_requirements(r."ContentJson") x
    WHERE strpos(r."ContentJson",'"requirement"')>0 AND x."BlockId"<>''
  ), saves AS MATERIALIZED (
    SELECT * FROM (
      SELECT b.*,lag(b."Fingerprint") OVER (PARTITION BY b."PageId",b."BlockId" ORDER BY b."Version") AS "Before" FROM blocks b) x
    WHERE "Before" IS DISTINCT FROM "Fingerprint"
  ), saved AS (
    INSERT INTO "ProjectorRequirementChange"("PageId","BlockId","RevisionId","Kind","Fingerprint","ChangedById","ChangedAt","Title","State")
    SELECT "PageId","BlockId","RevisionId",'save',"Fingerprint","AuthorId","ActiveFrom","Title","State" FROM saves ORDER BY "PageId","Version"
  )
  INSERT INTO "ProjectorRequirementChange"("PageId","BlockId","RevisionId","Kind","Fingerprint","ChangedById","ChangedAt","PublishedById","PublishedAt",
    "Title","State")
  SELECT x."PageId",x."BlockId",x."RevisionId",'publish',x."Fingerprint",coalesce(s."AuthorId",x."AuthorId"),coalesce(s."ActiveFrom",x."ActiveFrom"),
    coalesce(x."PublisherId",x."AuthorId"),x."PublishedAt",x."Title",x."State"
  FROM (
    SELECT b.*,u."PublisherId",u."ActiveFrom" AS "PublishedAt",
      lag(b."Fingerprint") OVER (PARTITION BY b."PageId",b."BlockId" ORDER BY u."ActiveFrom",u."Id") AS "Before"
    FROM "PagePublication" u JOIN blocks b ON b."PageId"=u."PageId" AND b."RevisionId"=u."RevisionId") x
  LEFT JOIN LATERAL (
    SELECT s."AuthorId",s."ActiveFrom" FROM saves s
    WHERE s."PageId"=x."PageId" AND s."BlockId"=x."BlockId" AND s."Fingerprint"=x."Fingerprint" AND s."Version"<=x."Version"
    ORDER BY s."Version" DESC LIMIT 1) s ON true
  WHERE x."Before" IS DISTINCT FROM x."Fingerprint"
  ORDER BY x."PageId",x."PublishedAt";
$$;

CREATE OR REPLACE FUNCTION atlas_rebuild_projector_requirements() RETURNS void LANGUAGE sql SET jit = off AS $$
  DELETE FROM "ProjectorRequirementIndex";
  DELETE FROM "ProjectorIndexedRevision";
  DELETE FROM "ProjectorRequirementChange";
  -- The history first: the refresh then finds every block's last save as it is and adds nothing.
  SELECT atlas_backfill_projector_history();
  SELECT count(atlas_refresh_projector_requirements(p."Id")) FROM "Page" p;
$$;

-- The existing rows: the new columns from their revisions (each walked once), then their pages' history.
WITH read AS MATERIALIZED (
  SELECT r."Id" AS "RevisionId",x.* FROM "PageRevision" r CROSS JOIN LATERAL atlas_projector_requirements(r."ContentJson") x
  WHERE r."Id" IN (SELECT DISTINCT "RevisionId" FROM "ProjectorRequirementIndex")
)
UPDATE "ProjectorRequirementIndex" i SET "PartyContactId"=x."PartyContactId",
  "Fingerprint"=atlas_projector_fingerprint(x."Key",x."Title",x."Direction",x."State",x."Date",x."Party",x."PartyContactId",x."Where",x."Requires")
FROM read x WHERE x."RevisionId"=i."RevisionId" AND x."Position"=i."Position";
SELECT atlas_backfill_projector_history();
