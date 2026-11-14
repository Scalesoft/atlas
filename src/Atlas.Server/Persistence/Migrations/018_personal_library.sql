CREATE TABLE "PageFavorite" (
  "Id" uuid PRIMARY KEY,
  "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
  "PageId" uuid NOT NULL REFERENCES "Page"("Id"),
  "ActiveFrom" timestamptz NOT NULL,
  "ActiveTo" timestamptz NULL,
  CHECK ("ActiveTo" IS NULL OR "ActiveTo">"ActiveFrom"),
  EXCLUDE USING gist ("UserId" WITH =,"PageId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_PageFavorite_Current" ON "PageFavorite"("UserId","PageId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_PageFavorite_Recent" ON "PageFavorite"("UserId","ActiveFrom" DESC,"PageId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_PageFavorite_History" ON "PageFavorite"("UserId","PageId","ActiveFrom" DESC);

-- Bounded operational navigation state: no historical copy for every read.
CREATE TABLE "PageVisit" (
  "Id" uuid PRIMARY KEY,
  "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
  "PageId" uuid NOT NULL REFERENCES "Page"("Id"),
  "LastVisitedAt" timestamptz NOT NULL,
  UNIQUE("UserId","PageId")
);
CREATE INDEX "IX_PageVisit_Recent" ON "PageVisit"("UserId","LastVisitedAt" DESC,"Id");

-- Rebuildable projection of immutable PageRevision authorship for fast personal draft lists.
CREATE TABLE "PageContribution" (
  "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
  "PageId" uuid NOT NULL REFERENCES "Page"("Id"),
  "LastRevisionVersion" integer NOT NULL,
  PRIMARY KEY("UserId","PageId"),
  FOREIGN KEY("PageId","LastRevisionVersion") REFERENCES "PageRevision"("PageId","Version")
);
INSERT INTO "PageContribution" SELECT "AuthorId","PageId",max("Version") FROM "PageRevision" GROUP BY "AuthorId","PageId";
CREATE FUNCTION atlas_track_page_contribution() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO "PageContribution"("UserId","PageId","LastRevisionVersion") VALUES(NEW."AuthorId",NEW."PageId",NEW."Version")
  ON CONFLICT("UserId","PageId") DO UPDATE SET "LastRevisionVersion"=greatest("PageContribution"."LastRevisionVersion",EXCLUDED."LastRevisionVersion");
  RETURN NEW;
END $$;
CREATE TRIGGER "TR_PageRevision_Contribution" AFTER INSERT ON "PageRevision" FOR EACH ROW EXECUTE FUNCTION atlas_track_page_contribution();
