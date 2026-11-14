-- Atlas 3.6 (shape 2026-11-04 §6, atlas-requirements 4): a requirement heading's party moves from its facts line into the heading's
-- attributes. The move is the job pages.requirements-headings (RequirementsHeadingsJob), the same as the block conversion, and repair on
-- open. Queued only where a working or published revision of a page not purged still declares version 3, so a new installation queues nothing.
INSERT INTO "JobRun"("JobId","Trigger","Reason","State")
SELECT 'pages.requirements-headings', 'event', 'Aktualizace na verzi 3.6: strana požadavku v nadpisu', 'queued'
WHERE EXISTS (SELECT 1 FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId"
  WHERE p."PurgedAt" IS NULL AND (r."ActiveTo" IS NULL OR r."Id" = p."PublishedRevisionId")
    AND strpos(r."ContentJson", '{"id":"atlas-requirements","version":3}') > 0)
ON CONFLICT DO NOTHING;
