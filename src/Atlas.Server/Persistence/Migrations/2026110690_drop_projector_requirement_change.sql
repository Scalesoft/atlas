-- Atlas 3.6 (owner): a requirement's history is read from the text, the dated lines of its notes and the state changes of the page's
-- versions (PageRevision.ChangeSummary), and who changed a block last from the newest revision whose summary touched it. The change
-- log of 2026103160 (ProjectorRequirementChange), its writers (the index refresh, atlas_record_projector_publication), its rebuild
-- (atlas_backfill_projector_history) and the fingerprint it compared go; the index and its triggers stay as they were.

-- 2026103160's refresh without the change log and the fingerprint.
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
    SELECT r."Id",r."ActiveTo" IS NULL AS "IsWorking",r."Id" IS NOT DISTINCT FROM p."PublishedRevisionId" AS "IsPublished",r."ContentJson"
    FROM "Page" p JOIN "PageRevision" r ON r."PageId"=p."Id" AND (r."ActiveTo" IS NULL OR r."Id"=p."PublishedRevisionId")
    WHERE p."Id"=page AND p."PurgedAt" IS NULL
      AND NOT EXISTS (SELECT 1 FROM "ProjectorIndexedRevision" x WHERE x."PageId"=page AND x."RevisionId"=r."Id")
  ), walked AS (
    INSERT INTO "ProjectorIndexedRevision"("PageId","RevisionId") SELECT page,"Id" FROM fresh
  )
  INSERT INTO "ProjectorRequirementIndex"("PageId","RevisionId","Position","IsWorking","IsPublished","BlockId","Key","Title","Direction",
    "State","Date","Party","Where","Requires","ParentBlockId","PartyContactId")
  SELECT page,f."Id",x."Position",f."IsWorking",f."IsPublished",x."BlockId",x."Key",x."Title",
    x."Direction",x."State",x."Date",x."Party",x."Where",x."Requires",x."ParentBlockId",x."PartyContactId"
  FROM fresh f CROSS JOIN LATERAL atlas_projector_requirements(f."ContentJson") x
  WHERE strpos(f."ContentJson",'"requirement"')>0;
$$;

CREATE OR REPLACE FUNCTION atlas_projector_requirements_trigger() RETURNS trigger LANGUAGE plpgsql SET jit = off AS $$
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

CREATE OR REPLACE FUNCTION atlas_rebuild_projector_requirements() RETURNS void LANGUAGE sql SET jit = off AS $$
  DELETE FROM "ProjectorRequirementIndex";
  DELETE FROM "ProjectorIndexedRevision";
  SELECT count(atlas_refresh_projector_requirements(p."Id")) FROM "Page" p;
$$;

DROP FUNCTION atlas_record_projector_publication(uuid);
DROP FUNCTION atlas_backfill_projector_history();
DROP TABLE "ProjectorRequirementChange";
ALTER TABLE "ProjectorRequirementIndex" DROP COLUMN "Fingerprint";
DROP FUNCTION atlas_projector_fingerprint(text, text, text, text, text, text, text, text, text);
