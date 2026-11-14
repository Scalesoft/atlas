-- Boards as nodes of the space tree (interaction spec §6): a board sits under a page (ParentPageId) or at the space root and
-- shares the pages' sibling order (SortOrder: gaps of 1024, ties by id), so pages and boards interleave. Every board of the
-- prototype goes to the end of its space's root, in title order. A trashed board gets a trash stamp of its own, as pages have
-- one; a page subtree's trash batch takes its boards along (IContentTreeParticipant). The tree's version counts board moves too.
ALTER TABLE "Board"
    ADD COLUMN "ParentPageId" uuid NULL REFERENCES "Page"("Id"),
    ADD COLUMN "SortOrder" integer NOT NULL DEFAULT 0,
    ADD COLUMN "TrashedAt" timestamptz NULL,
    ADD COLUMN "TrashedById" uuid NULL REFERENCES "UserAccount"("Id"),
    ADD COLUMN "TrashBatchId" uuid NULL;
WITH tail AS (
    SELECT s."Id" AS "SpaceId", COALESCE(max(p."SortOrder"), 0) AS tail
    FROM "Space" s LEFT JOIN "Page" p ON p."SpaceId" = s."Id" AND p."ParentId" IS NULL AND NOT p."IsTrashed"
    GROUP BY s."Id"
), ordered AS (
    SELECT b."Id", t.tail + 1024 * row_number() OVER (PARTITION BY b."SpaceId" ORDER BY b."Title", b."Id") AS position
    FROM "Board" b JOIN tail t ON t."SpaceId" = b."SpaceId"
)
UPDATE "Board" b SET "SortOrder" = o.position FROM ordered o WHERE o."Id" = b."Id";
UPDATE "Board" SET "TrashedAt" = "UpdatedAt", "TrashedById" = "CreatedBy", "TrashBatchId" = gen_random_uuid() WHERE "IsTrashed";
ALTER TABLE "Board"
    ALTER COLUMN "SortOrder" DROP DEFAULT,
    ADD CONSTRAINT "CK_Board_SortOrder" CHECK ("SortOrder" > 0),
    ADD CONSTRAINT "CK_Board_Trash" CHECK (
        (NOT "IsTrashed" AND "TrashedAt" IS NULL AND "TrashedById" IS NULL AND "TrashBatchId" IS NULL)
        OR ("IsTrashed" AND "TrashedAt" IS NOT NULL AND "TrashedById" IS NOT NULL AND "TrashBatchId" IS NOT NULL));
DROP INDEX "IX_Board_Space";
CREATE INDEX "IX_Board_Tree" ON "Board"("SpaceId", "ParentPageId", "SortOrder", "Id") WHERE NOT "IsTrashed";
CREATE INDEX "IX_Board_Parent" ON "Board"("ParentPageId") WHERE "ParentPageId" IS NOT NULL;
CREATE INDEX "IX_Board_Trash" ON "Board"("SpaceId", "TrashedAt" DESC, "Id") WHERE "IsTrashed";

-- The tree's concurrency token (SpaceOrganization) moves with every board placed, moved, trashed or restored, as with pages.
CREATE FUNCTION atlas_version_board_organization() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND ROW(NEW."SpaceId", NEW."ParentPageId", NEW."SortOrder", NEW."IsTrashed")
        IS NOT DISTINCT FROM ROW(OLD."SpaceId", OLD."ParentPageId", OLD."SortOrder", OLD."IsTrashed") THEN
        RETURN NULL;
    END IF;
    UPDATE "SpaceOrganization" SET "Version" = "Version" + 1 WHERE "SpaceId" = NEW."SpaceId";
    IF TG_OP = 'UPDATE' AND OLD."SpaceId" <> NEW."SpaceId" THEN
        UPDATE "SpaceOrganization" SET "Version" = "Version" + 1 WHERE "SpaceId" = OLD."SpaceId";
    END IF;
    RETURN NULL;
END $$;
CREATE TRIGGER "TR_Board_Organization" AFTER INSERT OR UPDATE OF "SpaceId", "ParentPageId", "SortOrder", "IsTrashed" ON "Board"
    FOR EACH ROW EXECUTE FUNCTION atlas_version_board_organization();

-- Search: each item's searchable text (its Markdown; a block's is the Markdown the page writer makes of it), and one document per
-- board with the title and the live items' texts, lexemes as the pages' (atlas_search_lexemes). The batch that changes a board
-- rewrites its document in the same transaction (atlas_refresh_board_search): a board's text spans many rows, so one rewrite per
-- batch instead of a trigger per changed item. A prototype block item gets its text with its next change.
ALTER TABLE "BoardItem" ADD COLUMN "SearchText" text NULL;
UPDATE "BoardItem" SET "SearchText" = "Text" WHERE "Kind" NOT IN ('page', 'block') AND "Text" IS NOT NULL;
CREATE TABLE "BoardSearchDocument" (
    "BoardId" uuid PRIMARY KEY REFERENCES "Board"("Id"),
    "Title" text NOT NULL,
    "Body" text NOT NULL,
    "Vector" tsvector NOT NULL
);
CREATE INDEX "IX_BoardSearchDocument_Vector" ON "BoardSearchDocument" USING gin ("Vector");
CREATE FUNCTION atlas_refresh_board_search(board uuid) RETURNS void LANGUAGE sql AS $$
    INSERT INTO "BoardSearchDocument"("BoardId", "Title", "Body", "Vector")
    SELECT d."Id", d."Title", d.body,
        setweight(atlas_search_lexemes(d."Title", atlas_normalize_search_text(d."Title")), 'A')
        || setweight(atlas_search_lexemes(d.body, atlas_normalize_search_text(d.body)), 'B')
    FROM (
        SELECT b."Id", b."Title", COALESCE((
            SELECT string_agg(i."SearchText", E'\n\n' ORDER BY i."Z", i."Id") FROM "BoardItem" i
            WHERE i."BoardId" = b."Id" AND NOT i."IsDeleted" AND i."SearchText" IS NOT NULL), '') AS body
        FROM "Board" b WHERE b."Id" = board) d
    ON CONFLICT ("BoardId") DO UPDATE SET "Title" = EXCLUDED."Title", "Body" = EXCLUDED."Body", "Vector" = EXCLUDED."Vector";
$$;
SELECT atlas_refresh_board_search("Id") FROM "Board";
