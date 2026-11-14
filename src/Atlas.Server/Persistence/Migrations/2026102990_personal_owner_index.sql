-- Můj prostor and its trash read the owner's private objects (PER1-5): without this they scanned every envelope.
CREATE INDEX "IX_ContentResource_Owner" ON "ContentResource"("OwnerId", "UpdatedAt" DESC) WHERE "Scope" = 'private';
