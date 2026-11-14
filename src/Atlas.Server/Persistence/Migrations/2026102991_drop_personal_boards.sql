-- The owner dropped personal boards on 29. 10. 2026 (spec 2026-10-29 §7): only private notes stay in Můj prostor. No release had them;
-- a development database may. Each one becomes a private note of its owner with the same id, so its shares, notifications and links
-- stay: the board's title, and as text the texts of its live cards, top to bottom, left to right, one paragraph each. A trashed board
-- becomes a trashed note (the owner's object trash). Then the board, its items, log and search document go, and the schema that only
-- personal boards used is undone: a board has a space again and no owner.
CREATE TEMPORARY TABLE personal_board_note ON COMMIT DROP AS
SELECT b."Id", b."OwnerId", b."Title", b."IsTrashed", b."TrashedAt", b."TrashedById",
       jsonb_build_object('title', b."Title", 'text', left(coalesce((
           SELECT string_agg(btrim(i."Text"), E'\n\n' ORDER BY i."Y", i."X", i."Id")
           FROM "BoardItem" i
           WHERE i."BoardId" = b."Id" AND NOT i."IsDeleted" AND btrim(coalesce(i."Text", '')) <> ''), ''), 100000)) AS "Data"
FROM "Board" b
WHERE b."OwnerId" IS NOT NULL;

UPDATE "ContentResource" r
SET "TypeId" = 'atlas.note', "Version" = 1, "Title" = n."Title", "Data" = n."Data",
    "TrashedAt" = CASE WHEN n."IsTrashed" THEN coalesce(n."TrashedAt", now()) END,
    "TrashedById" = CASE WHEN n."IsTrashed" THEN coalesce(n."TrashedById", n."OwnerId") END
FROM personal_board_note n
WHERE r."Id" = n."Id";
INSERT INTO "ObjectChange"("ObjectId", "Version", "ChangeId", "AuthorId", "AppliedAt", "PayloadJson")
SELECT n."Id", 1, gen_random_uuid(), n."OwnerId", now(), n."Data" FROM personal_board_note n;

-- The envelope stays the note's: the board's trigger would delete it with the board.
ALTER TABLE "Board" DISABLE TRIGGER "TR_Board_ContentResource";
DELETE FROM "BoardSearchDocument" WHERE "BoardId" IN (SELECT "Id" FROM personal_board_note);
DELETE FROM "BoardOperation" WHERE "BoardId" IN (SELECT "Id" FROM personal_board_note);
DELETE FROM "BoardItem" WHERE "BoardId" IN (SELECT "Id" FROM personal_board_note);
DELETE FROM "Board" WHERE "Id" IN (SELECT "Id" FROM personal_board_note);
ALTER TABLE "Board" ENABLE TRIGGER "TR_Board_ContentResource";

DROP INDEX "IX_Board_Owner";
ALTER TABLE "Board" DROP CONSTRAINT "CK_Board_Owner";
ALTER TABLE "Board" DROP COLUMN "OwnerId";
ALTER TABLE "Board" ALTER COLUMN "SpaceId" SET NOT NULL;

-- Every tracked row has a space again; the envelope keeps carrying a board's title (RelationNodes, the object lists).
CREATE OR REPLACE FUNCTION atlas_track_content_resource() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    current jsonb;
BEGIN
    IF TG_OP = 'DELETE' THEN
        DELETE FROM "ContentResource" WHERE "Id" = OLD."Id";
        RETURN NULL;
    END IF;
    current := to_jsonb(NEW);
    IF TG_OP = 'INSERT' THEN
        INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","Version","CreatedAt","CreatedBy","UpdatedAt","TrashedAt","Title")
        VALUES (NEW."Id", TG_ARGV[0], 'space', NEW."SpaceId", (current ->> TG_ARGV[1])::bigint, now(), (current ->> TG_ARGV[2])::uuid, now(),
            (current ->> 'TrashedAt')::timestamptz, current ->> 'Title');
    ELSE
        UPDATE "ContentResource" SET "SpaceId" = NEW."SpaceId", "Version" = (current ->> TG_ARGV[1])::bigint,
            "TrashedAt" = (current ->> 'TrashedAt')::timestamptz, "Title" = current ->> 'Title',
            "UpdatedAt" = CASE WHEN (current ->> TG_ARGV[1]) IS DISTINCT FROM (to_jsonb(OLD) ->> TG_ARGV[1]) THEN now() ELSE "UpdatedAt" END
        WHERE "Id" = NEW."Id";
    END IF;
    RETURN NULL;
END $$;
