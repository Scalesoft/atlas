-- Operational checkpoint acknowledgement; unknown legacy edits remain unacknowledged.
ALTER TABLE "PageCollaboration" ADD COLUMN "LastSavedSequence" bigint NULL;
UPDATE "PageCollaboration" SET "LastSavedSequence"=0 WHERE "Sequence"=0;
ALTER TABLE "PageCollaboration" ADD CONSTRAINT "CK_PageCollaboration_LastSaved" CHECK
    ("LastSavedSequence" IS NULL OR "LastSavedSequence" BETWEEN 0 AND "Sequence");

CREATE OR REPLACE FUNCTION atlas_version_page_organization() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
    IF TG_OP='UPDATE' AND ROW(NEW."SpaceId",NEW."ParentId",NEW."SortOrder",NEW."IsTrashed",NEW."TrashedAt",NEW."TrashedById",NEW."TrashBatchId")
        IS NOT DISTINCT FROM ROW(OLD."SpaceId",OLD."ParentId",OLD."SortOrder",OLD."IsTrashed",OLD."TrashedAt",OLD."TrashedById",OLD."TrashBatchId") THEN RETURN NEW; END IF;
    SELECT "ActiveFrom" INTO previous FROM "PageOrganizationRevision" WHERE "PageId"=NEW."Id" AND "ActiveTo" IS NULL;
    boundary := greatest(clock_timestamp(),previous+interval '1 microsecond');
    UPDATE "PageOrganizationRevision" SET "ActiveTo"=boundary WHERE "PageId"=NEW."Id" AND "ActiveTo" IS NULL;
    INSERT INTO "PageOrganizationRevision" VALUES(gen_random_uuid(),NEW."Id",NEW."SpaceId",NEW."ParentId",NEW."SortOrder",NEW."IsTrashed",NEW."TrashedAt",NEW."TrashedById",NEW."TrashBatchId",NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
    UPDATE "SpaceOrganization" SET "Version"="Version"+1 WHERE "SpaceId"=NEW."SpaceId";
    IF TG_OP='UPDATE' AND OLD."SpaceId"<>NEW."SpaceId" THEN
        UPDATE "SpaceOrganization" SET "Version"="Version"+1 WHERE "SpaceId"=OLD."SpaceId";
    END IF;
    RETURN NEW;
END $$;
