-- Atlas 3 groups and explicit membership (objects spec §5.4): an item belongs to a group or frame of its own board through
-- "ParentId", never itself and never as a connector (a connector follows its ends). Groups are a new kind like a frame.
-- The domain (BoardEditing) keeps the parent a live group or frame, without cycles and at most four levels deep.
-- The board's deferred keys are checked at once around the changes below: PostgreSQL alters no table with pending checks, and the
-- migrations of one run share a transaction.
SET CONSTRAINTS "FK_BoardItem_From", "FK_BoardItem_To" IMMEDIATE;
ALTER TABLE "BoardItem" ADD COLUMN "ParentId" uuid NULL,
    ADD CONSTRAINT "CK_BoardItem_Parent" CHECK ("ParentId" IS NULL OR ("ParentId" <> "Id" AND "Kind" <> 'connector')),
    ADD CONSTRAINT "FK_BoardItem_Parent" FOREIGN KEY ("BoardId","ParentId") REFERENCES "BoardItem"("BoardId","Id") DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE "BoardItem"
    DROP CONSTRAINT "CK_BoardItem_Shape",
    ADD CONSTRAINT "CK_BoardItem_Shape" CHECK (
        ("Kind" IN ('note','text','shape','frame','group','block') AND "TargetResourceId" IS NULL AND "FromItemId" IS NULL AND "ToItemId" IS NULL)
        OR ("Kind" = 'page' AND "TargetResourceId" IS NOT NULL AND "FromItemId" IS NULL AND "ToItemId" IS NULL)
        OR ("Kind" = 'connector' AND "TargetResourceId" IS NULL AND "FromItemId" IS NOT NULL AND "ToItemId" IS NOT NULL
            AND "FromItemId" <> "ToItemId"));

-- Until now a frame held what lay fully inside it. That becomes explicit once: every live item joins the smallest live frame
-- of its board that holds it and is larger than it (so two frames never hold each other).
UPDATE "BoardItem" item SET "ParentId" = (
    SELECT frame."Id" FROM "BoardItem" frame
    WHERE frame."BoardId" = item."BoardId" AND frame."Kind" = 'frame' AND NOT frame."IsDeleted" AND frame."Id" <> item."Id"
      AND item."X" >= frame."X" AND item."Y" >= frame."Y"
      AND item."X" + item."Width" <= frame."X" + frame."Width" AND item."Y" + item."Height" <= frame."Y" + frame."Height"
      AND frame."Width" * frame."Height" > item."Width" * item."Height"
    ORDER BY frame."Width" * frame."Height", frame."Id"
    LIMIT 1)
WHERE item."Kind" <> 'connector' AND NOT item."IsDeleted";

-- Frames nested deeper than four levels are cut every fifth level: an item at depth d ends at depth d mod 5.
WITH RECURSIVE chain AS (
    SELECT "Id", 0 AS depth FROM "BoardItem" WHERE "ParentId" IS NULL
    UNION ALL
    SELECT child."Id", chain.depth + 1 FROM "BoardItem" child JOIN chain ON child."ParentId" = chain."Id")
UPDATE "BoardItem" SET "ParentId" = NULL FROM chain WHERE chain."Id" = "BoardItem"."Id" AND chain.depth > 0 AND chain.depth % 5 = 0;
SET CONSTRAINTS "FK_BoardItem_From", "FK_BoardItem_To", "FK_BoardItem_Parent" IMMEDIATE;
SET CONSTRAINTS "FK_BoardItem_From", "FK_BoardItem_To", "FK_BoardItem_Parent" DEFERRED;
