-- A page's account mentions are read by the one C# reader of contact snapshots (ContactSnapshots, PageMentionNoticesHandler), so the
-- inbox and the entity catalog cannot disagree on them (review 2026-10-29 cycle 1, ARCH1-5). The SQL reader goes; the told mentions
-- stay. After a restore the entity catalog's rebuild (atlas_enqueue_entity_catalog) records them again without telling anybody.
DROP FUNCTION atlas_rebuild_page_mention_notices();
DROP FUNCTION atlas_page_account_mentions(text);
