-- A sign-out revokes only its random browser session id. Keep the fact permanently: cookie renewal can outlive sign-in history.
CREATE TABLE "RevokedBrowserSession" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "RevokedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_RevokedBrowserSession" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_RevokedBrowserSession_User" FOREIGN KEY ("UserId") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_RevokedBrowserSession_Time" CHECK (isfinite("RevokedAt"))
);
CREATE INDEX "IX_RevokedBrowserSession_User" ON "RevokedBrowserSession"("UserId");
CREATE TRIGGER "TR_RevokedBrowserSession_Immutable" BEFORE UPDATE OR DELETE ON "RevokedBrowserSession"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_immutable();
