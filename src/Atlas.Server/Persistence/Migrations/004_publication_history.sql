CREATE TABLE "PagePublication" (
  "Id" uuid PRIMARY KEY,
  "PageId" uuid NOT NULL REFERENCES "Page"("Id"),
  "RevisionId" uuid NOT NULL,
  "PublisherId" uuid NULL REFERENCES "UserAccount"("Id"),
  "ActiveFrom" timestamptz NOT NULL,
  "ActiveTo" timestamptz NULL,
  CONSTRAINT "FK_PagePublication_Revision" FOREIGN KEY ("PageId","RevisionId") REFERENCES "PageRevision"("PageId","Id"),
  CONSTRAINT "CK_PagePublication_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom")
);
CREATE UNIQUE INDEX "UX_PagePublication_Current" ON "PagePublication"("PageId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_PagePublication_History" ON "PagePublication"("PageId","ActiveFrom" DESC);
ALTER TABLE "PagePublication" ADD CONSTRAINT "EX_PagePublication_NoOverlap" EXCLUDE USING gist ("PageId" WITH =, tstzrange("ActiveFrom", "ActiveTo", '[)') WITH &&);
-- Earlier publication times and actors are unknown; preserve only the known current state.
INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom","ActiveTo")
SELECT gen_random_uuid(), "Id", "PublishedRevisionId", NULL, now(), NULL FROM "Page" WHERE "PublishedRevisionId" IS NOT NULL;
