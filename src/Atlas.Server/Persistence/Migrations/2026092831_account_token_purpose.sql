-- Invitation or reset (owner decision 2026-09-28): an invitation for a new account that has never set a password lasts seven days,
-- a password reset thirty minutes. The purpose is fixed when the code is issued. A code issued before this migration is an invitation
-- when no reset was ever requested for its account (account creation records none; every reset records credential.reset_requested).
ALTER TABLE "BuiltinAccountToken" ADD COLUMN "Purpose" varchar(16) NOT NULL DEFAULT 'reset'
    CONSTRAINT "CK_BuiltinAccountToken_Purpose" CHECK ("Purpose" IN ('invitation','reset'));
UPDATE "BuiltinAccountToken" token SET "Purpose"='invitation'
WHERE NOT EXISTS (
    SELECT 1 FROM "AccessAuditEvent" e JOIN "UserAccountRevision" r ON r."Id"=e."AccountRevisionId"
    WHERE r."UserId"=token."UserId" AND e."Action"='credential.reset_requested');
ALTER TABLE "BuiltinAccountToken" ALTER COLUMN "Purpose" DROP DEFAULT;
