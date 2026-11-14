-- Atlas 4.0: a deployment board draws the external services of a page's requirements (their `system`), and pages made before the catalog
-- named them have none. The job pages.requirement-systems (RequirementSystemsJob) fills an empty `system` of a requirement heading from
-- the catalog heading it came from (its `from`), in the working and published revisions, in place and without a backup (it only adds a value where there is none); it never
-- changes a value that is there. It waits for pages.requirements-headings, which writes the provenance. Queued only where a page is
-- marked as a catalog, so a new installation queues nothing.
INSERT INTO "JobRun"("JobId","Trigger","Reason","State")
SELECT 'pages.requirement-systems', 'event', 'Aktualizace na verzi 4.0: služby požadavků z katalogu', 'queued'
WHERE EXISTS (SELECT 1 FROM "ProjectorCatalogPage")
ON CONFLICT DO NOTHING;
