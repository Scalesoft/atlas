-- Atlas 3 object cards (objects spec §5.1): the kind 'object' shows any object on a board; its target is the object (the board's
-- view session and its placements read it there), its props name the object and its type (BoardProps). Existing rows keep their
-- kinds, so only the shape constraint changes. The board's deferred keys are checked at once around it, as in 2026102680.
SET CONSTRAINTS "FK_BoardItem_From", "FK_BoardItem_To", "FK_BoardItem_Parent" IMMEDIATE;
ALTER TABLE "BoardItem"
    DROP CONSTRAINT "CK_BoardItem_Shape",
    ADD CONSTRAINT "CK_BoardItem_Shape" CHECK (
        ("Kind" IN ('note','text','shape','frame','group','block') AND "TargetResourceId" IS NULL AND "FromItemId" IS NULL AND "ToItemId" IS NULL)
        OR ("Kind" IN ('page','object') AND "TargetResourceId" IS NOT NULL AND "FromItemId" IS NULL AND "ToItemId" IS NULL)
        OR ("Kind" = 'connector' AND "TargetResourceId" IS NULL AND "FromItemId" IS NOT NULL AND "ToItemId" IS NOT NULL
            AND "FromItemId" <> "ToItemId"));
SET CONSTRAINTS "FK_BoardItem_From", "FK_BoardItem_To", "FK_BoardItem_Parent" DEFERRED;
