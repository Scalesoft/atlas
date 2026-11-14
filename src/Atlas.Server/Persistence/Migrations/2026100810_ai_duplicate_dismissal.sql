-- Canonical unordered pairs only; decision retains neither a page title nor an actor identity.
CREATE TABLE "AiDuplicateDismissal" (
  "PageId" uuid NOT NULL CONSTRAINT "FK_AiDuplicateDismissal_Page" REFERENCES "Page"("Id") ON DELETE CASCADE,
  "OtherPageId" uuid NOT NULL CONSTRAINT "FK_AiDuplicateDismissal_OtherPage" REFERENCES "Page"("Id") ON DELETE CASCADE,
  "CreatedAt" timestamptz NOT NULL,
  CONSTRAINT "PK_AiDuplicateDismissal" PRIMARY KEY ("PageId","OtherPageId"),
  CONSTRAINT "CK_AiDuplicateDismissal_Order" CHECK ("PageId"<"OtherPageId")
);
CREATE INDEX "IX_AiDuplicateDismissal_OtherPage" ON "AiDuplicateDismissal"("OtherPageId");
