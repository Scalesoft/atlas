-- Atlas 3.5.1: a requirement history line written as "- 30. 10. 2026 — text" (the MCP help) was read as two nested ordered lists.
-- The parser escapes the date now; the code reads the stored shape repaired (LegacyDocumentRepair history_date_list, the editor's
-- legacyDocument.ts), and the job pages.requirements-overview rewrites the working and published revisions in place, history as stored.
-- Queued only where a page declares the plugin, as 2026110480.
INSERT INTO "JobRun"("JobId","Trigger","Reason","State")
SELECT 'pages.requirements-overview', 'event', 'Aktualizace na verzi 3.5.1 (data historie)', 'queued'
WHERE EXISTS (SELECT 1 FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId"
  WHERE p."PurgedAt" IS NULL AND (r."ActiveTo" IS NULL OR r."Id" = p."PublishedRevisionId") AND r."ContentJson" LIKE '%"atlas-requirements"%')
ON CONFLICT DO NOTHING;
