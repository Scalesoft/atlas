-- Atlas 3.6 (owner: the database holds no logic; Atlas has its own scheduler, Wolverine's durable jobs and the outbox): the requirement
-- index (ProjectorRequirementIndex, its marks ProjectorIndexedRevision) is no longer kept by triggers. They recomputed every page on every
-- save inside its transaction (and at its commit, deferred), which on large pages ran past the command timeout and failed the conversion
-- job. The index is kept in C# (Features/Projector/Persistence/ProjectorIndex.cs): the outbox's delivery of each page's ContentRevised
-- refreshes the page after the commit, and a read refreshes first what does not match its current revisions. The rows stay as they are
-- (the C# reading equals the SQL one, RequirementHeadingsParityTests); the job projector.requirements-index rebuilds them all once.
DROP TRIGGER "TR_Page_ProjectorRequirementIndex" ON "Page";
DROP TRIGGER "TR_PageRevision_ProjectorRequirementIndex" ON "PageRevision";
DROP TRIGGER "TR_PageRevision_ProjectorRequirementIndex_Update" ON "PageRevision";
DROP FUNCTION atlas_projector_requirements_trigger();
DROP FUNCTION atlas_rebuild_projector_requirements();
DROP FUNCTION atlas_refresh_projector_requirements(uuid, uuid);
DROP FUNCTION atlas_projector_requirements(text, uuid);
DROP FUNCTION atlas_projector_text(jsonb, boolean);
DROP FUNCTION atlas_projector_party(text);
DROP FUNCTION atlas_projector_deadline(text);

INSERT INTO "JobRun"("JobId","Trigger","Reason","State")
SELECT 'projector.requirements-index', 'event', 'Aktualizace na verzi 3.6 (index požadavků v kódu)', 'queued'
WHERE EXISTS (SELECT 1 FROM "Page" WHERE "PurgedAt" IS NULL)
ON CONFLICT DO NOTHING;
