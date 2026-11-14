-- Atlas 3.6: requirement blocks become requirement headings (atlas-requirements 3). The conversion is the job pages.requirements-headings
-- (RequirementsHeadingsJob), checkpointed and re-runnable; the originals are kept in RequirementConversionBackup (migration 2026110700).
-- Queued only where a working or published revision of a page not purged still holds a requirement block (the job's own test), so a new
-- installation queues nothing. Every restore of a backup requests it again (InstallationModules.PagesRebuild).
INSERT INTO "JobRun"("JobId","Trigger","Reason","State")
SELECT 'pages.requirements-headings', 'event', 'Aktualizace na verzi 3.6', 'queued'
WHERE EXISTS (SELECT 1 FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId"
  WHERE p."PurgedAt" IS NULL AND (r."ActiveTo" IS NULL OR r."Id" = p."PublishedRevisionId") AND strpos(r."ContentJson", '"type":"requirement"') > 0)
ON CONFLICT DO NOTHING;
