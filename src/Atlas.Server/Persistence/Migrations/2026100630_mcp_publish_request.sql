-- Publication over MCP needs a person (MCP plan C3, S0 decision D7.2): atlas_publish creates a request bound to the token, its owner,
-- the page and the version, and publishes only once the owner approved it in the Atlas web UI in their own cookie session. The
-- request lives ten minutes, is decided once and used once. The change summary the person saw is stored with it and is the one the
-- publication carries. Nothing here is content beyond that summary; the decision is audited through ApiActivity (mcp.publish).
CREATE TABLE "McpPublishRequest" (
    "Id" uuid CONSTRAINT "PK_McpPublishRequest" PRIMARY KEY,
    "TokenId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "PageId" uuid NOT NULL CONSTRAINT "FK_McpPublishRequest_Page" REFERENCES "Page"("Id") ON DELETE CASCADE,
    "Version" integer NOT NULL CONSTRAINT "CK_McpPublishRequest_Version" CHECK ("Version" > 0),
    "ChangeSummary" varchar(500) NULL,
    "CreatedAt" timestamptz NOT NULL,
    "ExpiresAt" timestamptz NOT NULL,
    "Decision" varchar(10) NULL CONSTRAINT "CK_McpPublishRequest_Decision" CHECK ("Decision" IN ('approved','declined')),
    "DecidedAt" timestamptz NULL,
    "ConsumedAt" timestamptz NULL,
    -- The owner is the token's owner: the pair is a foreign key (UX_PersonalApiToken_IdUser, 2026100620).
    CONSTRAINT "FK_McpPublishRequest_Token" FOREIGN KEY ("TokenId","UserId") REFERENCES "PersonalApiToken"("Id","UserId") ON DELETE CASCADE,
    CONSTRAINT "CK_McpPublishRequest_Window" CHECK ("ExpiresAt" > "CreatedAt" AND "ExpiresAt" <= "CreatedAt" + interval '10 minutes'),
    CONSTRAINT "CK_McpPublishRequest_Decided" CHECK (("Decision" IS NULL) = ("DecidedAt" IS NULL)),
    CONSTRAINT "CK_McpPublishRequest_Consumed" CHECK ("ConsumedAt" IS NULL OR "Decision" = 'approved')
);
CREATE INDEX "IX_McpPublishRequest_Open" ON "McpPublishRequest"("TokenId","PageId","Version","ExpiresAt") WHERE "ConsumedAt" IS NULL;
CREATE INDEX "IX_McpPublishRequest_User" ON "McpPublishRequest"("UserId","ExpiresAt") WHERE "ConsumedAt" IS NULL;

-- What a request is bound to never changes, a decision is made once and a use happens once, after an approval.
CREATE FUNCTION atlas_mcp_publish_request_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW."Id" <> OLD."Id" OR NEW."TokenId" <> OLD."TokenId" OR NEW."UserId" <> OLD."UserId" OR NEW."PageId" <> OLD."PageId"
        OR NEW."Version" <> OLD."Version" OR NEW."ChangeSummary" IS DISTINCT FROM OLD."ChangeSummary"
        OR NEW."CreatedAt" <> OLD."CreatedAt" OR NEW."ExpiresAt" <> OLD."ExpiresAt" THEN
        RAISE EXCEPTION 'McpPublishRequest binding is immutable' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD."Decision" IS NOT NULL AND (NEW."Decision" IS DISTINCT FROM OLD."Decision" OR NEW."DecidedAt" IS DISTINCT FROM OLD."DecidedAt") THEN
        RAISE EXCEPTION 'McpPublishRequest is already decided' USING ERRCODE = 'check_violation';
    END IF;
    IF OLD."ConsumedAt" IS NOT NULL AND NEW."ConsumedAt" IS DISTINCT FROM OLD."ConsumedAt" THEN
        RAISE EXCEPTION 'McpPublishRequest is already used' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_McpPublishRequest_Guard" BEFORE UPDATE ON "McpPublishRequest"
    FOR EACH ROW EXECUTE FUNCTION atlas_mcp_publish_request_guard();
