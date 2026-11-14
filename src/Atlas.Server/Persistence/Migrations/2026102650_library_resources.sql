-- Favorites and recent visits of any content resource (interaction spec §6: boards too, through IContentLibraryParticipant).
-- Every page is a content resource (2026102600), so every existing row stays as it is; the column keeps its name "PageId" and
-- now holds the resource's id. Page views (PageView) stay pages only.
ALTER TABLE "PageFavorite" DROP CONSTRAINT "PageFavorite_PageId_fkey",
    ADD CONSTRAINT "FK_PageFavorite_Resource" FOREIGN KEY ("PageId") REFERENCES "ContentResource"("Id");
ALTER TABLE "PageVisit" DROP CONSTRAINT "PageVisit_PageId_fkey",
    ADD CONSTRAINT "FK_PageVisit_Resource" FOREIGN KEY ("PageId") REFERENCES "ContentResource"("Id");
