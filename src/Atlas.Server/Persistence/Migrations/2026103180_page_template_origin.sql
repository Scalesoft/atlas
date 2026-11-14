-- Template origin (plan 3.1 §2.1, U1): the template a page was created from, so a template's „Použito na“ lists the pages made from it
-- (PageTemplateRelations). No foreign key: a template purged for good stays a tombstone row (PagePurge), and the installation backup
-- restores the table as it is. Pages created before 3.1 recorded no origin and keep none.
ALTER TABLE "Page" ADD COLUMN "TemplatePageId" uuid NULL;
CREATE INDEX "IX_Page_TemplatePageId" ON "Page" ("TemplatePageId") WHERE "TemplatePageId" IS NOT NULL;
