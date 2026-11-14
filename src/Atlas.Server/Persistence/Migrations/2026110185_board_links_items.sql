-- P15 „Tabule ukazují vazby samy“: the kind 'links' is a list the board derives from the relations (Lidé projektu, Na co čekáme,
-- Kde se používá); its props name what it shows and at most an object (BoardProps), never a target or ends, like a note. Existing
-- rows keep their kinds, so only the shape constraint changes. The board's deferred keys are checked at once around it, as in 2026102750.
SET CONSTRAINTS "FK_BoardItem_From", "FK_BoardItem_To", "FK_BoardItem_Parent" IMMEDIATE;
ALTER TABLE "BoardItem"
    DROP CONSTRAINT "CK_BoardItem_Shape",
    ADD CONSTRAINT "CK_BoardItem_Shape" CHECK (
        ("Kind" IN ('note','text','shape','frame','group','block','links') AND "TargetResourceId" IS NULL AND "FromItemId" IS NULL AND "ToItemId" IS NULL)
        OR ("Kind" IN ('page','object') AND "TargetResourceId" IS NOT NULL AND "FromItemId" IS NULL AND "ToItemId" IS NULL)
        OR ("Kind" = 'connector' AND "TargetResourceId" IS NULL AND "FromItemId" IS NOT NULL AND "ToItemId" IS NOT NULL
            AND "FromItemId" <> "ToItemId"));
SET CONSTRAINTS "FK_BoardItem_From", "FK_BoardItem_To", "FK_BoardItem_Parent" DEFERRED;
