-- Several pages export into one folder, each under its own PDF name. "Last*" is the file a page's export last wrote: the export
-- removes it once its name or folder changes, and no other page may take that name until then. Existing exports keep their file.
DROP INDEX "UX_PagePdfExport_Target";
CREATE UNIQUE INDEX "UX_PagePdfExport_Target" ON "PagePdfExport" ("StorageProfileId",lower("Folder"),lower("FileName"));
ALTER TABLE "PagePdfExport" ADD COLUMN "LastStorageProfileId" uuid NULL, ADD COLUMN "LastFolder" text NULL, ADD COLUMN "LastFileName" text NULL;
UPDATE "PagePdfExport" SET "LastStorageProfileId"="StorageProfileId","LastFolder"="Folder","LastFileName"="FileName" WHERE "LastExportedAt" IS NOT NULL;
