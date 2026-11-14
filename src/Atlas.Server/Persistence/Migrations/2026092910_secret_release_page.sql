-- The page a release was requested from, for Čtenost stránky (shown to managers of the vault only). Null for a release outside a
-- page and whenever the server could not confirm the page: the account cannot read it, or none of its revisions or its live
-- collaboration references the secret. Additive; no foreign key, because audit rows are immutable and must outlive a page.
ALTER TABLE "SecretReleaseEvent" ADD COLUMN "PageId" uuid NULL;
CREATE INDEX "IX_SecretReleaseEvent_Page" ON "SecretReleaseEvent"("PageId","OccurredAt" DESC) WHERE "PageId" IS NOT NULL;
