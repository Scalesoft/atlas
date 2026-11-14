-- Atlas 3.4 (plan R6): the deployment summary requirementSummary is the overview of kind requirements and atlas-requirements has schema 2.
-- The code reads both shapes (LegacyDocumentRepair, the editor's legacyDocument.ts, the schema's alias for live documents); stored content
-- is converted by the job pages.requirements-overview (RequirementsOverviewJob) in checkpointed batches after the start: the working and
-- published revisions in place, history as stored. Queued only where a page declares the plugin, so a new installation queues nothing.
-- Every restore of a backup requests it again (InstallationModules.PagesRebuild).
INSERT INTO "JobRun"("JobId","Trigger","Reason","State")
SELECT 'pages.requirements-overview', 'event', 'Aktualizace na verzi 3.4', 'queued'
WHERE EXISTS (SELECT 1 FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId"
  WHERE p."PurgedAt" IS NULL AND (r."ActiveTo" IS NULL OR r."Id" = p."PublishedRevisionId") AND r."ContentJson" LIKE '%"atlas-requirements"%')
ON CONFLICT DO NOTHING;
