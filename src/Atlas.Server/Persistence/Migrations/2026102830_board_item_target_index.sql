-- Atlas 3 space contents and relations (spec 2026-10-27 §7, §13.3). Where an object is placed, across every board: the placement
-- relation reads the live cards of a target (incoming placements, "Kde je použito"), which only the board's own items index answered.
CREATE INDEX "IX_BoardItem_Target" ON "BoardItem"("TargetResourceId", "BoardId") WHERE NOT "IsDeleted" AND "TargetResourceId" IS NOT NULL;
-- The live objects of one space, every type: the contents listing and the space side of every relation source start here.
CREATE INDEX "IX_ContentResource_Space" ON "ContentResource"("SpaceId", "TypeId") WHERE "TrashedAt" IS NULL;
