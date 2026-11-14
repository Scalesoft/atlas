ALTER TABLE "Page" ADD COLUMN "SortOrder" integer NOT NULL DEFAULT 0;
ALTER TABLE "Page" ADD COLUMN "IsTrashed" boolean NOT NULL DEFAULT false;
ALTER TABLE "Page" ADD COLUMN "TrashedAt" timestamptz NULL;
ALTER TABLE "Page" ADD COLUMN "TrashedById" uuid NULL REFERENCES "UserAccount"("Id");
ALTER TABLE "Page" ADD COLUMN "TrashBatchId" uuid NULL;
WITH ordered AS (
    SELECT p."Id", (row_number() OVER (PARTITION BY p."SpaceId",p."ParentId" ORDER BY r."Title",p."Id") * 1024)::integer AS position
    FROM "Page" p JOIN "PageRevision" r ON r."PageId"=p."Id" AND r."ActiveTo" IS NULL
) UPDATE "Page" p SET "SortOrder"=o.position FROM ordered o WHERE o."Id"=p."Id";
ALTER TABLE "Page" ADD CONSTRAINT "CK_Page_SortOrder" CHECK ("SortOrder">0);
ALTER TABLE "Page" ADD CONSTRAINT "CK_Page_Trash" CHECK (
    (NOT "IsTrashed" AND "TrashedAt" IS NULL AND "TrashedById" IS NULL AND "TrashBatchId" IS NULL)
    OR ("IsTrashed" AND "TrashedAt" IS NOT NULL AND "TrashedById" IS NOT NULL AND "TrashBatchId" IS NOT NULL));
CREATE INDEX "IX_Page_CurrentTree" ON "Page"("SpaceId","ParentId","SortOrder","Id") WHERE NOT "IsTrashed";
CREATE INDEX "IX_Page_Trash" ON "Page"("SpaceId","TrashedAt" DESC,"Id") WHERE "IsTrashed";
CREATE INDEX "IX_Page_TrashBatch" ON "Page"("TrashBatchId") WHERE "IsTrashed";

CREATE TABLE "SpaceOrganization" (
    "SpaceId" uuid PRIMARY KEY REFERENCES "Space"("Id"),
    "Version" bigint NOT NULL DEFAULT 1 CHECK ("Version">0)
);
INSERT INTO "SpaceOrganization"("SpaceId") SELECT "Id" FROM "Space";
CREATE FUNCTION atlas_initialize_space_organization() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN INSERT INTO "SpaceOrganization"("SpaceId") VALUES(NEW."Id"); RETURN NEW; END $$;
CREATE TRIGGER "TR_Space_Organization" AFTER INSERT ON "Space" FOR EACH ROW EXECUTE FUNCTION atlas_initialize_space_organization();

CREATE TABLE "PageOrganizationRevision" (
    "Id" uuid PRIMARY KEY,
    "PageId" uuid NOT NULL REFERENCES "Page"("Id"),
    "SpaceId" uuid NOT NULL REFERENCES "Space"("Id"),
    "ParentId" uuid NULL REFERENCES "Page"("Id"),
    "SortOrder" integer NOT NULL CHECK ("SortOrder">0),
    "IsTrashed" boolean NOT NULL,
    "TrashedAt" timestamptz NULL,
    "TrashedById" uuid NULL REFERENCES "UserAccount"("Id"),
    "TrashBatchId" uuid NULL,
    "ActorId" uuid NULL REFERENCES "UserAccount"("Id"),
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CHECK ("ActiveTo" IS NULL OR "ActiveTo">"ActiveFrom")
);
CREATE UNIQUE INDEX "UX_PageOrganizationRevision_Current" ON "PageOrganizationRevision"("PageId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_PageOrganizationRevision_History" ON "PageOrganizationRevision"("PageId","ActiveFrom" DESC);
ALTER TABLE "PageOrganizationRevision" ADD CONSTRAINT "EX_PageOrganizationRevision_NoOverlap"
    EXCLUDE USING gist ("PageId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&);
INSERT INTO "PageOrganizationRevision"
SELECT gen_random_uuid(),"Id","SpaceId","ParentId","SortOrder","IsTrashed","TrashedAt","TrashedById","TrashBatchId",NULL,now(),NULL FROM "Page";

CREATE FUNCTION atlas_prepare_page_organization() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE tail integer;
BEGIN
    IF TG_OP='INSERT' THEN
        PERFORM 1 FROM "Space" WHERE "Id"=NEW."SpaceId" FOR UPDATE;
        IF NEW."ParentId" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM "Page" WHERE "Id"=NEW."ParentId" AND "SpaceId"=NEW."SpaceId" AND NOT "IsTrashed") THEN
            RAISE EXCEPTION 'Parent must be active in the same space' USING ERRCODE='23514';
        END IF;
        IF NEW."SortOrder"=0 THEN
            SELECT COALESCE(max("SortOrder"),0) INTO tail FROM "Page" WHERE "SpaceId"=NEW."SpaceId" AND "ParentId" IS NOT DISTINCT FROM NEW."ParentId" AND NOT "IsTrashed";
            IF tail>2147482623 THEN RAISE EXCEPTION 'Sibling order requires normalization' USING ERRCODE='23514'; END IF;
            NEW."SortOrder" := tail+1024;
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_Page_PrepareOrganization" BEFORE INSERT ON "Page" FOR EACH ROW EXECUTE FUNCTION atlas_prepare_page_organization();

CREATE FUNCTION atlas_version_page_organization() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
    IF TG_OP='UPDATE' AND ROW(NEW."SpaceId",NEW."ParentId",NEW."SortOrder",NEW."IsTrashed",NEW."TrashedAt",NEW."TrashedById",NEW."TrashBatchId")
        IS NOT DISTINCT FROM ROW(OLD."SpaceId",OLD."ParentId",OLD."SortOrder",OLD."IsTrashed",OLD."TrashedAt",OLD."TrashedById",OLD."TrashBatchId") THEN RETURN NEW; END IF;
    SELECT "ActiveFrom" INTO previous FROM "PageOrganizationRevision" WHERE "PageId"=NEW."Id" AND "ActiveTo" IS NULL;
    boundary := greatest(clock_timestamp(),previous+interval '1 microsecond');
    UPDATE "PageOrganizationRevision" SET "ActiveTo"=boundary WHERE "PageId"=NEW."Id" AND "ActiveTo" IS NULL;
    INSERT INTO "PageOrganizationRevision" VALUES(gen_random_uuid(),NEW."Id",NEW."SpaceId",NEW."ParentId",NEW."SortOrder",NEW."IsTrashed",NEW."TrashedAt",NEW."TrashedById",NEW."TrashBatchId",NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
    UPDATE "SpaceOrganization" SET "Version"="Version"+1 WHERE "SpaceId"=NEW."SpaceId";
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_Page_OrganizationHistory" AFTER INSERT OR UPDATE ON "Page" FOR EACH ROW EXECUTE FUNCTION atlas_version_page_organization();
