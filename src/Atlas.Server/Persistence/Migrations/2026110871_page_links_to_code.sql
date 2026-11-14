-- 3.6 "no logic in the database": the page links are derived by code (PageLinkRevision, an outbox delivery of ContentRevised, and the
-- rebuild job pages.links-rebuild). The trigger and the functions go; the existing rows stay and the job rebuilds them from the
-- published revisions, a batch at a time, while the application runs.
DROP TRIGGER "TR_Page_PageLink" ON "Page";
DROP FUNCTION atlas_page_links_trigger();
DROP FUNCTION atlas_rebuild_page_links();
DROP FUNCTION atlas_refresh_page_links(uuid);
DROP FUNCTION atlas_page_link_targets(text);
INSERT INTO "JobRun"("JobId","Trigger","Reason","State") SELECT 'pages.links-rebuild','event','Odkazy mezi stránkami počítá kód','queued'
-- A new installation has no pages to rebuild (as in 2026110870).
WHERE EXISTS (SELECT 1 FROM "Page" WHERE "PurgedAt" IS NULL)
ON CONFLICT DO NOTHING;
