-- Pinned spaces. The pin policy is part of the space settings,
-- so it is versioned in SpaceMetadataRevision and moves the settings CAS. Existing spaces and their revisions become 'Default'
-- (owner decision 2026-09-28): the sidebar keeps showing every space a reader had, and each reader can unpin what they don't
-- need. A space created from now on starts as 'None'.
ALTER TABLE "Space" ADD COLUMN "PinPolicy" varchar(10) NOT NULL DEFAULT 'Default'
    CONSTRAINT "CK_Space_PinPolicy" CHECK ("PinPolicy" IN ('None','Default','Required'));
ALTER TABLE "Space" ALTER COLUMN "PinPolicy" SET DEFAULT 'None';
ALTER TABLE "SpaceMetadataRevision" ADD COLUMN "PinPolicy" varchar(10) NOT NULL DEFAULT 'Default'
    CONSTRAINT "CK_SpaceMetadataRevision_PinPolicy" CHECK ("PinPolicy" IN ('None','Default','Required'));
-- The default only backfills; the trigger below names every column it writes.
ALTER TABLE "SpaceMetadataRevision" ALTER COLUMN "PinPolicy" DROP DEFAULT;

CREATE OR REPLACE FUNCTION atlas_version_space_metadata() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
 IF TG_OP='UPDATE' AND ROW(NEW."Name",NEW."Icon",NEW."Color",NEW."Description",NEW."PinPolicy")
   IS NOT DISTINCT FROM ROW(OLD."Name",OLD."Icon",OLD."Color",OLD."Description",OLD."PinPolicy") THEN RETURN NEW; END IF;
 SELECT "ActiveFrom" INTO previous FROM "SpaceMetadataRevision" WHERE "SpaceId"=NEW."Id" AND "ActiveTo" IS NULL;
 boundary := greatest(clock_timestamp(),previous + interval '1 microsecond');
 UPDATE "SpaceMetadataRevision" SET "ActiveTo"=boundary WHERE "SpaceId"=NEW."Id" AND "ActiveTo" IS NULL;
 INSERT INTO "SpaceMetadataRevision"("Id","SpaceId","Name","Icon","Color","Description","PinPolicy","ChangedBy","ActiveFrom","ActiveTo")
 VALUES(gen_random_uuid(),NEW."Id",NEW."Name",NEW."Icon",NEW."Color",NEW."Description",NEW."PinPolicy",
   NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
 RETURN NEW;
END $$;

-- The account's own choice, versioned like PageFavorite: Pinned=false is an explicit unpin (of a Default space), no current row
-- means no choice of its own. Losing access or a policy change keeps the rows.
CREATE TABLE "SpacePin" (
  "Id" uuid PRIMARY KEY,
  "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
  "SpaceId" uuid NOT NULL REFERENCES "Space"("Id"),
  "Pinned" boolean NOT NULL,
  "ActiveFrom" timestamptz NOT NULL,
  "ActiveTo" timestamptz NULL,
  CHECK ("ActiveTo" IS NULL OR "ActiveTo">"ActiveFrom"),
  EXCLUDE USING gist ("UserId" WITH =,"SpaceId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_SpacePin_Current" ON "SpacePin"("UserId","SpaceId") WHERE "ActiveTo" IS NULL;
