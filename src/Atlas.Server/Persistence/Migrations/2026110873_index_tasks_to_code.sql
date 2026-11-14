-- 3.6 "no logic in the database": the AI index tasks are enqueued by code (IndexTaskRevision, a delivery of ContentRevised, and the
-- space's inclusion change in SaveSpaceAiSettingsHandler). The two triggers and their functions go. Nothing to rebuild: the index's
-- own consistency check (IndexStore.EnqueueInconsistentAsync) enqueues what a live generation misses, and a database without a live
-- generation has no tasks to lose.
DROP TRIGGER "TR_Page_IndexTask" ON "Page";
DROP TRIGGER "TR_SpaceAiSetting_IndexTask" ON "SpaceAiSetting";
DROP FUNCTION atlas_index_task_trigger();
DROP FUNCTION atlas_space_ai_index_trigger();
DROP FUNCTION atlas_enqueue_index_sync(uuid);
