-- Replacing a page from the source editor (space Admin or WikiAdmin) is a page audit fact pinned to the revision it created.
ALTER TABLE "PageAccessEvent" DROP CONSTRAINT "CK_PageAccessEvent_Action";
ALTER TABLE "PageAccessEvent" ADD CONSTRAINT "CK_PageAccessEvent_Action" CHECK (
    ("Action"='page.opened' AND "ActorId" IS NOT NULL AND "ShareLinkId" IS NULL) OR
    ("Action"='share.opened' AND "ActorId" IS NULL AND "ShareLinkId" IS NOT NULL) OR
    ("Action"='share.created' AND "ActorId" IS NOT NULL AND "ShareLinkId" IS NOT NULL) OR
    ("Action"='share.revoked' AND "ShareLinkId" IS NOT NULL) OR
    ("Action"='page.source-edited' AND "ActorId" IS NOT NULL AND "ShareLinkId" IS NULL));
