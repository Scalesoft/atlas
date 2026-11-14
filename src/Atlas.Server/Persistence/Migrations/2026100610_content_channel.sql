-- Channel (transport) and personal API token of every change, publication and page opening (Atlas 2.0, MCP track T2).
-- No table rewrite: ADD COLUMN with a constant default stores 'legacy' only in the catalog (atthasmissing), and dropping the
-- default right away makes every later INSERT name its channel. The CHECK and FK validations and the partial index each read
-- the table once; nothing is rewritten. PageAccessEvent gets nullable columns only (old events and share events stay NULL).
ALTER TABLE "PageRevision"
  ADD COLUMN "Channel" varchar(8) NOT NULL DEFAULT 'legacy',
  ADD COLUMN "ApiTokenId" uuid NULL CONSTRAINT "FK_PageRevision_ApiToken" REFERENCES "PersonalApiToken"("Id");
ALTER TABLE "PageRevision" ALTER COLUMN "Channel" DROP DEFAULT;
ALTER TABLE "PageRevision" ADD CONSTRAINT "CK_PageRevision_Channel"
  CHECK ("Channel" IN ('web','api','mcp','import','system','legacy') AND (("Channel" IN ('api','mcp')) = ("ApiTokenId" IS NOT NULL)));
CREATE INDEX "IX_PageRevision_ApiToken" ON "PageRevision"("ApiTokenId") WHERE "ApiTokenId" IS NOT NULL;

-- Publishing creates no revision, so the publication carries its own channel and token.
ALTER TABLE "PagePublication"
  ADD COLUMN "Channel" varchar(8) NOT NULL DEFAULT 'legacy',
  ADD COLUMN "ApiTokenId" uuid NULL CONSTRAINT "FK_PagePublication_ApiToken" REFERENCES "PersonalApiToken"("Id");
ALTER TABLE "PagePublication" ALTER COLUMN "Channel" DROP DEFAULT;
ALTER TABLE "PagePublication" ADD CONSTRAINT "CK_PagePublication_Channel"
  CHECK ("Channel" IN ('web','api','mcp','import','system','legacy') AND (("Channel" IN ('api','mcp')) = ("ApiTokenId" IS NOT NULL)));
CREATE INDEX "IX_PagePublication_ApiToken" ON "PagePublication"("ApiTokenId") WHERE "ApiTokenId" IS NOT NULL;

-- Only page.opened carries a channel; NULL is an event from before 2.0 or one without a channel (share links, audit triggers).
ALTER TABLE "PageAccessEvent"
  ADD COLUMN "Channel" varchar(8) NULL,
  ADD COLUMN "ApiTokenId" uuid NULL CONSTRAINT "FK_PageAccessEvent_ApiToken" REFERENCES "PersonalApiToken"("Id");
ALTER TABLE "PageAccessEvent" ADD CONSTRAINT "CK_PageAccessEvent_Channel" CHECK (
  ("Channel" IS NULL AND "ApiTokenId" IS NULL) OR
  ("Action" = 'page.opened' AND "Channel" IN ('web','api','mcp','system') AND (("Channel" IN ('api','mcp')) = ("ApiTokenId" IS NOT NULL))));
