-- Atlas 3.4 removes the 3.3 deployment board (an experiment, the owner: "zatím tam nic není"): a page's boards of the purpose
-- projector.deployment go with their items, log and search document (as BoardPurge deletes a board), and the page cards that showed a
-- requirement block (display "requirement") on other boards are deleted as any item is (IsDeleted), with the connectors attached to
-- them. The text of the pages stays. A frame's "lane" mark, only the deployment board's planner set, is dropped from the frames left.
-- The boards that lost cards or marks get their search document again, and their sequence moves on: the change has no batch in
-- their log, so an open client that catches up from its sequence gets resync_required and loads the board again. A restore of an older backup runs this script again
-- (InstallationModules.DataMigrations): no colon or question mark outside the comments, NHibernate would read it as a parameter.
CREATE TEMPORARY TABLE deployment_board ON COMMIT DROP AS
SELECT "Id" FROM "Board" WHERE "Purpose" = 'projector.deployment';

UPDATE "ResourceGrant" SET "ActiveTo" = greatest(clock_timestamp(), "ActiveFrom" + interval '1 microsecond')
  WHERE "ObjectId" IN (SELECT "Id" FROM deployment_board) AND "ActiveTo" IS NULL;
DELETE FROM "PageFavorite" WHERE "PageId" IN (SELECT "Id" FROM deployment_board);
DELETE FROM "PageVisit" WHERE "PageId" IN (SELECT "Id" FROM deployment_board);
DELETE FROM "Comment" WHERE "ObjectId" IN (SELECT "Id" FROM deployment_board);
DELETE FROM "EntityOccurrence" WHERE "ResourceId" IN (SELECT "Id" FROM deployment_board);
DELETE FROM "ObjectChange" WHERE "ObjectId" IN (SELECT "Id" FROM deployment_board);
DELETE FROM "TreeReference" WHERE "TargetId" IN (SELECT "Id" FROM deployment_board);
DELETE FROM "BoardSearchDocument" WHERE "BoardId" IN (SELECT "Id" FROM deployment_board);
DELETE FROM "BoardOperation" WHERE "BoardId" IN (SELECT "Id" FROM deployment_board);
DELETE FROM "BoardItem" WHERE "BoardId" IN (SELECT "Id" FROM deployment_board);
DELETE FROM "Board" WHERE "Id" IN (SELECT "Id" FROM deployment_board);

CREATE TEMPORARY TABLE changed_board ON COMMIT DROP AS
SELECT DISTINCT "BoardId" AS "Id" FROM "BoardItem" WHERE NOT "IsDeleted"
  AND (("Kind" = 'page' AND "PropsJson" ->> 'display' = 'requirement') OR ("Kind" = 'frame' AND jsonb_exists("PropsJson", 'lane')));

UPDATE "BoardItem" c SET "IsDeleted" = true, "Version" = c."Version" + 1
WHERE NOT c."IsDeleted" AND (
    (c."Kind" = 'page' AND c."PropsJson" ->> 'display' = 'requirement')
    OR (c."Kind" = 'connector' AND EXISTS (
        SELECT 1 FROM "BoardItem" p WHERE p."BoardId" = c."BoardId" AND p."Id" IN (c."FromItemId", c."ToItemId")
          AND p."Kind" = 'page' AND p."PropsJson" ->> 'display' = 'requirement')));

UPDATE "BoardItem" SET "PropsJson" = "PropsJson" - 'lane', "Version" = "Version" + 1 WHERE "Kind" = 'frame' AND jsonb_exists("PropsJson", 'lane');

UPDATE "Board" SET "Sequence" = "Sequence" + 1 WHERE "Id" IN (SELECT "Id" FROM changed_board);
SELECT atlas_refresh_board_search("Id") FROM changed_board;
