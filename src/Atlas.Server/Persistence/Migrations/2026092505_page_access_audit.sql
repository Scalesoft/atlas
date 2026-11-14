-- Forward-only audit facts, following AccessAuditEvent's dialect. No synthetic legacy backfill.
ALTER TABLE "PageShareLink" ADD CONSTRAINT "UX_PageShareLink_PageId_Id" UNIQUE ("PageId","Id");
CREATE TABLE "PageAccessEvent" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY CONSTRAINT "PK_PageAccessEvent" PRIMARY KEY,
    "PageId" uuid NOT NULL,
    "RevisionId" uuid NOT NULL,
    "ActorId" uuid NULL,
    "ShareLinkId" uuid NULL,
    "Action" varchar(30) NOT NULL,
    "OccurredAt" timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT "FK_PageAccessEvent_Page" FOREIGN KEY ("PageId") REFERENCES "Page"("Id") ON DELETE RESTRICT,
    -- Pin the exact revision delivered, not whatever is current when an administrator reads history.
    CONSTRAINT "FK_PageAccessEvent_Revision" FOREIGN KEY ("PageId","RevisionId") REFERENCES "PageRevision"("PageId","Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_PageAccessEvent_Actor" FOREIGN KEY ("ActorId") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_PageAccessEvent_Link" FOREIGN KEY ("PageId","ShareLinkId") REFERENCES "PageShareLink"("PageId","Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_PageAccessEvent_Time" CHECK (isfinite("OccurredAt")),
    CONSTRAINT "CK_PageAccessEvent_Action" CHECK (
        ("Action"='page.opened' AND "ActorId" IS NOT NULL AND "ShareLinkId" IS NULL) OR
        ("Action"='share.opened' AND "ActorId" IS NULL AND "ShareLinkId" IS NOT NULL) OR
        ("Action"='share.created' AND "ActorId" IS NOT NULL AND "ShareLinkId" IS NOT NULL) OR
        ("Action"='share.revoked' AND "ShareLinkId" IS NOT NULL))
);
CREATE INDEX "IX_PageAccessEvent_Page" ON "PageAccessEvent"("PageId","Id" DESC);
CREATE INDEX "IX_PageAccessEvent_Actor" ON "PageAccessEvent"("ActorId");
CREATE INDEX "IX_PageAccessEvent_LastRead" ON "PageAccessEvent"("ActorId","PageId","OccurredAt" DESC) WHERE "Action"='page.opened';
CREATE INDEX "IX_PageAccessEvent_Link" ON "PageAccessEvent"("ShareLinkId","OccurredAt" DESC);
CREATE INDEX "IX_PageAccessEvent_Revision" ON "PageAccessEvent"("RevisionId");
CREATE TRIGGER "TR_PageAccessEvent_Immutable" BEFORE UPDATE OR DELETE ON "PageAccessEvent"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_immutable();

-- Covers explicit and automatic revocation, atomically with the capability mutation.
CREATE FUNCTION atlas_audit_page_share() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        INSERT INTO "PageAccessEvent"("PageId","RevisionId","ActorId","ShareLinkId","Action")
        VALUES(NEW."PageId",NEW."RevisionId",NEW."CreatedById",NEW."Id",'share.created');
    ELSIF OLD."ActiveTo" IS NULL AND NEW."ActiveTo" IS NOT NULL THEN
        INSERT INTO "PageAccessEvent"("PageId","RevisionId","ActorId","ShareLinkId","Action")
        VALUES(NEW."PageId",NEW."RevisionId",NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,NEW."Id",'share.revoked');
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_PageShareLink_Audit" AFTER INSERT OR UPDATE ON "PageShareLink"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_page_share();
