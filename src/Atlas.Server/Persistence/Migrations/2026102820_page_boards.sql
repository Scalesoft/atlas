-- A page's own board (Projector's deployment board, spec 2026-10-27 §1): Purpose names what the board is for, and such a board
-- belongs to its parent page: it is not a node of the space tree, the page decides who reads and edits it, and a page has at most
-- one live board of each purpose. Existing boards have none and stay as they are.
ALTER TABLE "Board"
    ADD COLUMN "Purpose" text NULL,
    ADD CONSTRAINT "CK_Board_Purpose" CHECK ("Purpose" IS NULL OR ("ParentPageId" IS NOT NULL AND "Purpose" ~ '^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*)+$'));
CREATE UNIQUE INDEX "UX_Board_PagePurpose" ON "Board"("ParentPageId", "Purpose") WHERE "Purpose" IS NOT NULL AND NOT "IsTrashed";
