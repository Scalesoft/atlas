-- Atlas 3.6 (model 2026-11-03 §10b, Rollback): before pages.requirements-headings (RequirementsHeadingsJob) rewrites a working or
-- published revision from requirement blocks to requirement headings, it keeps the revision's stored JSON and Markdown here, kept
-- until the next release. RolledBack is set on every row of a page an administrator restored from here: the job, which is re-runnable,
-- then never converts that page again. A purge deletes the rows with the page's content (PagePurge); a restore of a backup starts
-- the table empty (BackupCoverage.Operational).
-- This migration only creates the table; migration 2026110830 queues the job.
CREATE TABLE "RequirementConversionBackup" (
  "RevisionId" uuid NOT NULL CONSTRAINT "PK_RequirementConversionBackup" PRIMARY KEY,
  "PageId" uuid NOT NULL CONSTRAINT "FK_RequirementConversionBackup_Page" REFERENCES "Page"("Id") ON DELETE CASCADE,
  "ContentJson" text NOT NULL,
  "Markdown" text NOT NULL,
  "ConvertedAt" timestamptz NOT NULL DEFAULT now(),
  "RolledBack" boolean NOT NULL DEFAULT false);
CREATE INDEX "IX_RequirementConversionBackup_PageId" ON "RequirementConversionBackup"("PageId");
