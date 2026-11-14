-- 3.6, no logic in the database (stream S4): the access predicates live in the query text of the code that asks them (AccessSql;
-- AccessSqlParityTests pinned each against its function). atlas_space_in_ai is still called by database functions of other streams
-- (search, AI index) until their own migrations drop them: it goes here once nothing calls it, else with the last of them.
DROP FUNCTION atlas_object_grant_role(uuid, uuid);
DROP FUNCTION atlas_may_create_below_page(uuid, uuid, text);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                   WHERE n.nspname = current_schema() AND p.proname <> 'atlas_space_in_ai' AND p.prosrc LIKE '%atlas_space_in_ai%') THEN
        DROP FUNCTION atlas_space_in_ai(uuid);
    END IF;
END $$;
