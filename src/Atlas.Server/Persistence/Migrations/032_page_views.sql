-- Immutable observed views; incomplete navigation history is deliberately not backfilled.
CREATE TABLE "PageView" (
  "Id" uuid CONSTRAINT "PK_PageView" PRIMARY KEY,
  "PageId" uuid NOT NULL CONSTRAINT "FK_PageView_Page" REFERENCES "Page"("Id") ON DELETE RESTRICT,
  "UserId" uuid NOT NULL CONSTRAINT "FK_PageView_UserAccount" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
  "ViewedAt" timestamptz NOT NULL
);
CREATE INDEX "IX_PageView_UserPageTime" ON "PageView"("UserId","PageId","ViewedAt" DESC);
CREATE INDEX "IX_PageView_PageTime" ON "PageView"("PageId","ViewedAt" DESC,"Id");
CREATE INDEX "IX_PageView_TimePage" ON "PageView"("ViewedAt","PageId");
CREATE INDEX "IX_PageRevision_Activity" ON "PageRevision"("ActiveFrom","PageId");
CREATE INDEX "IX_PageRevision_AuthorActivity" ON "PageRevision"("AuthorId","ActiveFrom","PageId");
