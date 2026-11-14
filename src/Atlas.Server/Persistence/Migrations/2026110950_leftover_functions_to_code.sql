-- Atlas 3.6, no logic in the database (plan 2026-11-04, stream S5): the functions that no trigger and no search owned. The index
-- store states "the page belongs in the index" and writes the page-neighbour lists in C# (IndexStore: the same rules, the same lock
-- key), and the entity catalog's rebuild lists its resources in C# (AnnounceCatalogRebuild). Nothing is rewritten: the lists and
-- tasks already stored are what the functions wrote, and the statements that replace them are pinned to the same output.
DROP FUNCTION atlas_ai_index_eligible("Page");
DROP FUNCTION atlas_ai_index_candidate("Page");
DROP FUNCTION atlas_refresh_page_neighbors(uuid, uuid);
DROP FUNCTION atlas_refresh_generation_neighbors(uuid, uuid, int, timestamptz);
DROP FUNCTION atlas_insert_own_page_neighbors(uuid, uuid[]);
DROP FUNCTION atlas_lock_page_neighbors(uuid);
DROP FUNCTION atlas_entity_catalog_resources();
-- Nothing has called the assistant's dependency status since the sources are checked by the page adapter in C#; search's migration
-- 2026110935 drops it first, this one only on a database that skipped it.
DROP FUNCTION IF EXISTS atlas_ai_dependency_status(uuid, text, uuid[]);
