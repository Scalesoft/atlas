-- Mutable automation settings and derived execution state; PageRevision is the durable source of pending work.
CREATE TABLE "PagePdfExport" (
 "PageId" uuid PRIMARY KEY REFERENCES "Page"("Id") ON DELETE CASCADE,
 "StorageProfileId" uuid NOT NULL REFERENCES "StorageProfile"("Id") ON DELETE RESTRICT,
 "OwnerId" uuid NOT NULL REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
 "Enabled" boolean NOT NULL DEFAULT true,
 "Folder" text NOT NULL, "FileName" text NOT NULL,
 "KeepVersions" boolean NOT NULL DEFAULT false, "PublishedOnly" boolean NOT NULL DEFAULT false,
 "SourceOrigin" text NOT NULL, "Version" integer NOT NULL DEFAULT 1 CHECK ("Version">0),
 "LastRevisionId" uuid NULL REFERENCES "PageRevision"("Id") ON DELETE SET NULL,
 "LastConfigurationVersion" integer NOT NULL DEFAULT 0, "LastProfileVersion" integer NOT NULL DEFAULT 0,
 "LastExportedAt" timestamptz NULL, "LastError" text NOT NULL DEFAULT '',
 "FailureCount" integer NOT NULL DEFAULT 0 CHECK ("FailureCount">=0),
 "NextAttemptAt" timestamptz NOT NULL DEFAULT now(), "RunNowRevisionId" uuid NULL REFERENCES "PageRevision"("Id") ON DELETE SET NULL
);
CREATE UNIQUE INDEX "UX_PagePdfExport_Target" ON "PagePdfExport" ("StorageProfileId",lower("Folder"));
CREATE INDEX "IX_PagePdfExport_Owner" ON "PagePdfExport"("OwnerId");
CREATE INDEX "IX_PagePdfExport_Revision" ON "PagePdfExport"("LastRevisionId");
CREATE INDEX "IX_PagePdfExport_Due" ON "PagePdfExport"("NextAttemptAt") WHERE "Enabled";

CREATE INDEX "IX_PagePdfExport_RunRevision" ON "PagePdfExport"("RunNowRevisionId");
