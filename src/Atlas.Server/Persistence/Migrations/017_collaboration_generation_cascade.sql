-- Preserve existing log rows when a known additive host schema changes generation.
ALTER TABLE "CollaborationUpdate"
    DROP CONSTRAINT "CollaborationUpdate_PageId_Generation_fkey";
ALTER TABLE "CollaborationUpdate"
    ADD CONSTRAINT "CollaborationUpdate_PageId_Generation_fkey"
    FOREIGN KEY ("PageId", "Generation") REFERENCES "PageCollaboration"("PageId", "Generation") ON UPDATE CASCADE;
