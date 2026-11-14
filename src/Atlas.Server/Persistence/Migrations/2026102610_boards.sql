-- Atlas 3 boards (boards spec §4). Every module's migrations run whether it is enabled or not, so a wiki without boards has
-- these tables empty. A board, its items and its operation log; the board's ContentResource row comes from the trigger.
CREATE TABLE "Board" (
    "Id" uuid PRIMARY KEY,
    "SpaceId" uuid NOT NULL REFERENCES "Space"("Id"),
    "Title" varchar(200) NOT NULL CONSTRAINT "CK_Board_Title" CHECK (length(btrim("Title")) > 0),
    -- The sequence of the last committed batch; every batch locks this row FOR UPDATE and adds one.
    "Sequence" bigint NOT NULL CONSTRAINT "CK_Board_Sequence" CHECK ("Sequence" >= 0),
    "CreatedAt" timestamptz NOT NULL,
    "CreatedBy" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "UpdatedAt" timestamptz NOT NULL,
    "IsTrashed" boolean NOT NULL
);
CREATE INDEX "IX_Board_Space" ON "Board"("SpaceId") WHERE NOT "IsTrashed";
CREATE TRIGGER "TR_Board_ContentResource" AFTER INSERT OR UPDATE OF "SpaceId" OR DELETE ON "Board"
    FOR EACH ROW EXECUTE FUNCTION atlas_track_content_resource('atlas.board');

-- Version is the sequence of the last change of the item. Items are only soft-deleted, so connector ends always exist; the
-- composite keys keep both ends on the board of the connector. A batch must create an end before its connector (the rules
-- refuse one whose end does not exist yet); the keys are deferred only so the order in which the rows are flushed does not matter.
CREATE TABLE "BoardItem" (
    "Id" uuid PRIMARY KEY,
    "BoardId" uuid NOT NULL REFERENCES "Board"("Id"),
    "Kind" varchar(16) NOT NULL,
    "X" double precision NOT NULL,
    "Y" double precision NOT NULL,
    "Width" double precision NOT NULL,
    "Height" double precision NOT NULL,
    "Z" integer NOT NULL,
    "Text" varchar(4000) NULL,
    "Color" varchar(16) NULL CONSTRAINT "CK_BoardItem_Color" CHECK ("Color" IN ('yellow','blue','green','pink','purple','gray')),
    "TargetResourceId" uuid NULL,
    "FromItemId" uuid NULL,
    "ToItemId" uuid NULL,
    "Version" bigint NOT NULL CONSTRAINT "CK_BoardItem_Version" CHECK ("Version" > 0),
    "IsDeleted" boolean NOT NULL,
    CONSTRAINT "UX_BoardItem_BoardItem" UNIQUE ("BoardId","Id"),
    CONSTRAINT "CK_BoardItem_Shape" CHECK (
        ("Kind" = 'note' AND "TargetResourceId" IS NULL AND "FromItemId" IS NULL AND "ToItemId" IS NULL)
        OR ("Kind" = 'page' AND "TargetResourceId" IS NOT NULL AND "FromItemId" IS NULL AND "ToItemId" IS NULL)
        OR ("Kind" = 'connector' AND "TargetResourceId" IS NULL AND "FromItemId" IS NOT NULL AND "ToItemId" IS NOT NULL
            AND "FromItemId" <> "ToItemId")),
    -- NaN sorts above every number and the infinities lie outside the bounds, so this also keeps non-finite values out.
    CONSTRAINT "CK_BoardItem_Geometry" CHECK ("X" BETWEEN -1000000 AND 1000000 AND "Y" BETWEEN -1000000 AND 1000000
        AND "Width" BETWEEN 8 AND 20000 AND "Height" BETWEEN 8 AND 20000),
    CONSTRAINT "FK_BoardItem_From" FOREIGN KEY ("BoardId","FromItemId") REFERENCES "BoardItem"("BoardId","Id") DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT "FK_BoardItem_To" FOREIGN KEY ("BoardId","ToItemId") REFERENCES "BoardItem"("BoardId","Id") DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX "IX_BoardItem_Live" ON "BoardItem"("BoardId") WHERE NOT "IsDeleted";

-- One row per committed batch: catching up after a disconnect (operations?after) and the idempotency of a retried operationId.
-- shortcut: the whole log is kept; compaction and a retention window come with the persistence chapter of the Atlas 3 plan.
CREATE TABLE "BoardOperation" (
    "BoardId" uuid NOT NULL REFERENCES "Board"("Id"),
    "Sequence" bigint NOT NULL CONSTRAINT "CK_BoardOperation_Sequence" CHECK ("Sequence" > 0),
    "OperationId" uuid NOT NULL,
    "AuthorId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "AppliedAt" timestamptz NOT NULL,
    "ChangesJson" text NOT NULL,
    PRIMARY KEY ("BoardId","Sequence"),
    CONSTRAINT "UX_BoardOperation_Operation" UNIQUE ("BoardId","OperationId")
);
