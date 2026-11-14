-- PER3-4: a permanently deleted page indexes nothing. The purge empties its revisions' ContentJson, and the deferred trigger of that
-- update ran this refresh at the commit, after the purge had deleted the page's rows, so it walked the emptied revisions and wrote
-- their ProjectorIndexedRevision rows again. A tombstone (Page.PurgedAt) is now never walked; the rows the old refresh wrote go.
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
    "State","Date","Party","Where","Requires","ParentBlockId")
  SELECT page,f."Id",x."Position",f."IsWorking",f."IsPublished",x."BlockId",x."Key",x."Title",
    x."Direction",x."State",x."Date",x."Party",x."Where",x."Requires",x."ParentBlockId"
  FROM fresh f CROSS JOIN LATERAL atlas_projector_requirements(f."ContentJson") x
  WHERE strpos(f."ContentJson",'"requirement"')>0;
$$;

DELETE FROM "ProjectorRequirementIndex" i USING "Page" p WHERE p."Id"=i."PageId" AND p."PurgedAt" IS NOT NULL;
DELETE FROM "ProjectorIndexedRevision" x USING "Page" p WHERE p."Id"=x."PageId" AND p."PurgedAt" IS NOT NULL;
-- PER3-1: the change summaries the earlier purge kept.
UPDATE "PagePublication" u SET "ChangeSummary"=NULL FROM "Page" p WHERE p."Id"=u."PageId" AND p."PurgedAt" IS NOT NULL AND u."ChangeSummary" IS NOT NULL;
