CREATE TABLE "WikiImportSource" ("Id" uuid PRIMARY KEY, "SourceKey" varchar(80) NOT NULL UNIQUE);
CREATE TABLE "WikiImportDestination" (
    "Id" uuid PRIMARY KEY, "SourceId" uuid NOT NULL REFERENCES "WikiImportSource"("Id"),
    "SpaceId" uuid NOT NULL REFERENCES "Space"("Id"), "ActiveFrom" timestamptz NOT NULL, "ActiveTo" timestamptz,
    CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    EXCLUDE USING gist ("SourceId" WITH =, tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&));
CREATE UNIQUE INDEX "IX_WikiImportDestination_Current" ON "WikiImportDestination"("SourceId") WHERE "ActiveTo" IS NULL;
CREATE TABLE "WikiImportPage" (
    "Id" uuid PRIMARY KEY, "SourceId" uuid NOT NULL REFERENCES "WikiImportSource"("Id"),
    "SourcePath" varchar(1024) NOT NULL, "PageId" uuid NOT NULL REFERENCES "Page"("Id"));
CREATE UNIQUE INDEX "IX_WikiImportPage_Path" ON "WikiImportPage"("SourceId",lower("SourcePath"));
CREATE TABLE "WikiImportCheckpoint" (
    "Id" uuid PRIMARY KEY, "ImportPageId" uuid NOT NULL REFERENCES "WikiImportPage"("Id"),
    "RevisionId" uuid NOT NULL REFERENCES "PageRevision"("Id"), "SourceSha256" char(64) NOT NULL,
    "ActiveFrom" timestamptz NOT NULL, "ActiveTo" timestamptz,
    CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    EXCLUDE USING gist ("ImportPageId" WITH =, tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&));
CREATE UNIQUE INDEX "IX_WikiImportCheckpoint_Current" ON "WikiImportCheckpoint"("ImportPageId") WHERE "ActiveTo" IS NULL;
-- Operational preview/confirmation journal, not editable business content. Original bytes are stored privately by batch UUID.
CREATE TABLE "WikiImportBatch" (
    "Id" uuid PRIMARY KEY, "SourceId" uuid NOT NULL REFERENCES "WikiImportSource"("Id"),
    "CreatedBy" uuid NOT NULL REFERENCES "UserAccount"("Id"), "CreatedAt" timestamptz NOT NULL,
    "ExpiresAt" timestamptz NOT NULL, "CompletedAt" timestamptz,
    "ArchiveSha256" char(64) NOT NULL, "OptionsJson" text NOT NULL, "ReportJson" text NOT NULL);
CREATE INDEX "IX_WikiImportBatch_Expiry" ON "WikiImportBatch"("ExpiresAt") WHERE "CompletedAt" IS NULL;
