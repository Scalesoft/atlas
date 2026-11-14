-- Atlas 3 board interactions (interaction spec §4): typed props per kind, the new kinds, the wider palette and the board's
-- settings. The prototype's rows get their kind's defaults: notes none, page cards the card display, connectors the elbow route
-- with an arrow at the end and the other keys their props require (BoardProps). Boards keep the default settings ('{}': nothing
-- changed, BoardProps.WithDefaults shows the defaults). Still a target only on page cards and ends only on connectors.
ALTER TABLE "Board" ADD COLUMN "SettingsJson" jsonb NOT NULL DEFAULT '{}'
    CONSTRAINT "CK_Board_Settings" CHECK (jsonb_typeof("SettingsJson") = 'object');
ALTER TABLE "BoardItem" ADD COLUMN "PropsJson" jsonb NOT NULL DEFAULT '{}'
    CONSTRAINT "CK_BoardItem_Props" CHECK (jsonb_typeof("PropsJson") = 'object');
UPDATE "BoardItem" SET "PropsJson" = '{"display":"card"}' WHERE "Kind" = 'page';
UPDATE "BoardItem" SET "PropsJson" = '{"arrowEnd":"arrow","arrowStart":"none","dash":"solid","route":"elbow"}' WHERE "Kind" = 'connector';
ALTER TABLE "BoardItem"
    DROP CONSTRAINT "CK_BoardItem_Color",
    ADD CONSTRAINT "CK_BoardItem_Color" CHECK ("Color" IN ('yellow','orange','red','pink','purple','blue','teal','green','gray','white','black')),
    DROP CONSTRAINT "CK_BoardItem_Shape",
    ADD CONSTRAINT "CK_BoardItem_Shape" CHECK (
        ("Kind" IN ('note','text','shape','frame','block') AND "TargetResourceId" IS NULL AND "FromItemId" IS NULL AND "ToItemId" IS NULL)
        OR ("Kind" = 'page' AND "TargetResourceId" IS NOT NULL AND "FromItemId" IS NULL AND "ToItemId" IS NULL)
        OR ("Kind" = 'connector' AND "TargetResourceId" IS NULL AND "FromItemId" IS NOT NULL AND "ToItemId" IS NOT NULL
            AND "FromItemId" <> "ToItemId"));
