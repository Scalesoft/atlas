-- 3.6, no logic in the database (plan 2026-11-04): the last of it. Each stream dropped its own triggers and functions; what a fully
-- migrated database still holds goes here (pg_proc outside the system schemas and the extensions, pg_trigger not internal).
-- atlas_space_in_ai stayed at 2026110946 while the index's eligibility functions still called it; 2026110950 dropped those.
-- AccessSql.SpaceInAi is its rule in code. SchemaWithoutLogicTests keeps the schema free of any new one.
DROP FUNCTION IF EXISTS atlas_space_in_ai(uuid);
