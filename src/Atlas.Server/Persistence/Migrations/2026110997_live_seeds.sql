-- Atlas 3.6 (one editor, J1.1): the server keeps the live state (Yjs) of every page's current revision, so the page editor builds once
-- from the page bundle and never seeds it. New writes are seeded by the delivery LiveSeedRevision; the job collaboration.live-seeds seeds
-- the existing pages that lack a current state, a batch at a time, while the application runs (each page as the browser would have).
INSERT INTO "JobRun"("JobId","Trigger","Reason","State")
SELECT 'collaboration.live-seeds', 'event', 'Aktualizace na verzi 3.6 (živý stav stránek ze serveru)', 'queued'
WHERE EXISTS (SELECT 1 FROM "Page" WHERE NOT "IsTrashed")
ON CONFLICT DO NOTHING;
