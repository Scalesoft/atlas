-- 3.6 "no logic in the database": the contact mentions of pages are derived by code (PageObjectMentionRevision, an outbox delivery of
-- ContentRevised, and the rebuild job contacts.mentions-rebuild). The triggers and the functions go; the existing rows stay and the
-- job rebuilds them from the published and working revisions, a batch at a time, while the application runs.
DROP TRIGGER "TR_Page_PageObjectMention" ON "Page";
DROP TRIGGER "TR_Page_PageObjectMention_Space" ON "Page";
DROP TRIGGER "TR_PageRevision_PageObjectMention" ON "PageRevision";
DROP TRIGGER "TR_PageRevision_PageObjectMention_Content" ON "PageRevision";
DROP FUNCTION atlas_page_object_mentions_trigger();
DROP FUNCTION atlas_page_revision_object_mentions_trigger();
DROP FUNCTION atlas_rebuild_page_object_mentions();
DROP FUNCTION atlas_refresh_page_object_mentions(uuid);
DROP FUNCTION atlas_page_object_mentions(text, uuid);
INSERT INTO "JobRun"("JobId","Trigger","Reason","State") SELECT 'contacts.mentions-rebuild','event','Zmínky kontaktů počítá kód','queued'
-- A new installation has no pages to rebuild (as in 2026110870).
WHERE EXISTS (SELECT 1 FROM "Page" WHERE "PurgedAt" IS NULL)
ON CONFLICT DO NOTHING;
