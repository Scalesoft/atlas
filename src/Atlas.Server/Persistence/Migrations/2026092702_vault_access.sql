-- Vault and secret access rights (WP4 V3). Additive: no vault grant exists after upgrade, so only WikiAdmin and the space Admin of a
-- vault's space reach it (owner decisions 27. 9.: both manage every vault they reach, like pages). Page roles give no vault access.
-- ResourceGrant gets typed targets next to PageId (WP3 D1): a vault grant (VaultId, all secrets of the vault) or a secret grant
-- (SecretId, with its VaultId). VaultSpaceId repeats the vault's space for sargable space filters and the audit view; the
-- composite foreign keys keep it and the secret's vault consistent, and a vault never changes its space.
-- Role of a vault or secret grant is an access level, a staircase like the document roles (VaultAccessLevel): 10 Vidí
-- (Discover), 20 Čte (Read), 40 Spravuje (Manage); 30 stays free for Zapisuje (writing values, not in 1.1).
-- An archived vault or a removed secret keeps its grant rows (history, and a secret restored under the same id), but
-- authorization resolves the target as missing, so the grants have no effect.

ALTER TABLE "Vault" ADD CONSTRAINT "UX_Vault_IdSpace" UNIQUE ("Id","SpaceId");
ALTER TABLE "VaultSecret" ADD CONSTRAINT "UX_VaultSecret_IdVault" UNIQUE ("Id","VaultId");

ALTER TABLE "ResourceGrant" ALTER COLUMN "PageId" DROP NOT NULL;
ALTER TABLE "ResourceGrant" ADD COLUMN "VaultId" uuid NULL;
ALTER TABLE "ResourceGrant" ADD COLUMN "SecretId" uuid NULL;
ALTER TABLE "ResourceGrant" ADD COLUMN "VaultSpaceId" uuid NULL;
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "FK_ResourceGrant_Vault"
    FOREIGN KEY ("VaultId","VaultSpaceId") REFERENCES "Vault"("Id","SpaceId") ON DELETE RESTRICT;
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "FK_ResourceGrant_Secret"
    FOREIGN KEY ("SecretId","VaultId") REFERENCES "VaultSecret"("Id","VaultId") ON DELETE RESTRICT;
-- Exactly one target: a page, a vault (no SecretId) or a secret of that vault.
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "CK_ResourceGrant_Target" CHECK (
    ("PageId" IS NOT NULL AND "VaultId" IS NULL AND "SecretId" IS NULL AND "VaultSpaceId" IS NULL) OR
    ("PageId" IS NULL AND "VaultId" IS NOT NULL AND "VaultSpaceId" IS NOT NULL));
ALTER TABLE "ResourceGrant" DROP CONSTRAINT "CK_ResourceGrant_Role";
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "CK_ResourceGrant_Role" CHECK (
    ("PageId" IS NOT NULL AND "Role" BETWEEN 1 AND 3) OR
    ("PageId" IS NULL AND "Role" IN (10,20,40)));

-- One current assignment of a subject per vault and per secret (the page constraints of 2026092601 ignore NULL PageId).
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "EX_ResourceGrant_VaultUserNoOverlap"
    EXCLUDE USING gist ("VaultId" WITH =,"UserId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
    WHERE ("VaultId" IS NOT NULL AND "SecretId" IS NULL AND "UserId" IS NOT NULL);
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "EX_ResourceGrant_VaultGroupNoOverlap"
    EXCLUDE USING gist ("VaultId" WITH =,"GroupId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
    WHERE ("VaultId" IS NOT NULL AND "SecretId" IS NULL AND "GroupId" IS NOT NULL);
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "EX_ResourceGrant_SecretUserNoOverlap"
    EXCLUDE USING gist ("SecretId" WITH =,"UserId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
    WHERE ("SecretId" IS NOT NULL AND "UserId" IS NOT NULL);
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "EX_ResourceGrant_SecretGroupNoOverlap"
    EXCLUDE USING gist ("SecretId" WITH =,"GroupId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
    WHERE ("SecretId" IS NOT NULL AND "GroupId" IS NOT NULL);
-- Every grant of a vault (vault and secret level) for authorization, listings and the access directory, and the latest
-- closed interval of a subject on a target for Temporal.NextBoundary. Subject lookups (security/me) use IX_ResourceGrant_User
-- and IX_ResourceGrant_Group of 2026092601, which cover every target.
CREATE INDEX "IX_ResourceGrant_VaultHistory" ON "ResourceGrant"("VaultId","SecretId","UserId","GroupId","ActiveTo" DESC)
    WHERE "VaultId" IS NOT NULL;

-- Same columns and order as 2026092601; VaultId and SecretId are appended. A vault grant event reports the vault's space.
CREATE OR REPLACE VIEW "AccessAuditView" AS
SELECT e."Id",e."ActorId",actor."DisplayName" AS "ActorName",e."OccurredAt",e."Action",
    COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId") AS "UserId",
    COALESCE(account."DisplayName",subject."DisplayName") AS "UserName",
    COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId") AS "GroupId",
    COALESCE(group_revision."Name",user_group."Name") AS "GroupName",
    COALESCE(grant_row."SpaceId",page."SpaceId",resource."VaultSpaceId") AS "SpaceId",space."Name" AS "SpaceName",
    COALESCE(grant_row."Role",resource."Role") AS "Role",
    account."IsActive",account."IsWikiAdmin",permission."CanIssueTokens",
    e."AccountRevisionId",e."GroupRevisionId",e."GroupMemberId",e."SpaceGrantId",e."ApiPermissionId",e."ResourceGrantId",
    resource."PageId",resource."VaultId",resource."SecretId"
FROM "AccessAuditEvent" e
LEFT JOIN "UserAccount" actor ON actor."Id"=e."ActorId"
LEFT JOIN "UserAccountRevision" account ON account."Id"=e."AccountRevisionId"
LEFT JOIN "UserGroupRevision" group_revision ON group_revision."Id"=e."GroupRevisionId"
LEFT JOIN "GroupMember" member ON member."Id"=e."GroupMemberId"
LEFT JOIN "SpaceGrant" grant_row ON grant_row."Id"=e."SpaceGrantId"
LEFT JOIN "UserApiPermission" permission ON permission."Id"=e."ApiPermissionId"
LEFT JOIN "ResourceGrant" resource ON resource."Id"=e."ResourceGrantId"
LEFT JOIN "Page" page ON page."Id"=resource."PageId"
LEFT JOIN "UserAccount" subject ON subject."Id"=COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId")
LEFT JOIN "UserGroup" user_group ON user_group."Id"=COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId")
LEFT JOIN "Space" space ON space."Id"=COALESCE(grant_row."SpaceId",page."SpaceId",resource."VaultSpaceId");

-- Forward-only: dropping vault grants would silently remove access and its history.
