ALTER TABLE "Space" ADD COLUMN "Description" varchar(2000) NOT NULL DEFAULT '';
CREATE TABLE "SpaceMetadataRevision" (
 "Id" uuid PRIMARY KEY, "SpaceId" uuid NOT NULL REFERENCES "Space"("Id"),
 "Name" varchar(200) NOT NULL, "Icon" varchar(40) NOT NULL, "Color" varchar(20) NOT NULL,
 "Description" varchar(2000) NOT NULL, "ChangedBy" uuid NULL REFERENCES "UserAccount"("Id"),
 "ActiveFrom" timestamptz NOT NULL, "ActiveTo" timestamptz NULL,
 CHECK ("ActiveTo" IS NULL OR "ActiveTo">"ActiveFrom")
);
CREATE UNIQUE INDEX "UX_SpaceMetadataRevision_Current" ON "SpaceMetadataRevision"("SpaceId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_SpaceMetadataRevision_History" ON "SpaceMetadataRevision"("SpaceId","ActiveFrom" DESC);
ALTER TABLE "SpaceMetadataRevision" ADD CONSTRAINT "EX_SpaceMetadataRevision_NoOverlap"
 EXCLUDE USING gist ("SpaceId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&);
INSERT INTO "SpaceMetadataRevision"
 SELECT gen_random_uuid(),"Id","Name","Icon","Color","Description",NULL,clock_timestamp(),NULL FROM "Space";
CREATE FUNCTION atlas_version_space_metadata() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
 IF TG_OP='UPDATE' AND ROW(NEW."Name",NEW."Icon",NEW."Color",NEW."Description")
   IS NOT DISTINCT FROM ROW(OLD."Name",OLD."Icon",OLD."Color",OLD."Description") THEN RETURN NEW; END IF;
 SELECT "ActiveFrom" INTO previous FROM "SpaceMetadataRevision" WHERE "SpaceId"=NEW."Id" AND "ActiveTo" IS NULL;
 boundary := greatest(clock_timestamp(),previous + interval '1 microsecond');
 UPDATE "SpaceMetadataRevision" SET "ActiveTo"=boundary WHERE "SpaceId"=NEW."Id" AND "ActiveTo" IS NULL;
 INSERT INTO "SpaceMetadataRevision" VALUES(gen_random_uuid(),NEW."Id",NEW."Name",NEW."Icon",NEW."Color",NEW."Description",
   NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
 RETURN NEW;
END $$;
CREATE TRIGGER "TR_Space_MetadataHistory" AFTER INSERT OR UPDATE ON "Space" FOR EACH ROW EXECUTE FUNCTION atlas_version_space_metadata();
