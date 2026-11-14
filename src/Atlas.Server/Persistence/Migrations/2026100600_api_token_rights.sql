-- Token rights (MCP plan T1): the per-account ceiling becomes a level, token scopes read/draft/publish, token channels rest/mcp.
-- Upgrade from 1.5 changes nothing a token may do: CanIssueTokens = true becomes level publish, scope write becomes draft + publish,
-- and every existing token gets the channel rest.

-- Level of the ceiling. The history guard lets an UPDATE change ActiveTo only, so the backfill runs with it disabled; the
-- migration transaction re-enables it before anything else can write.
ALTER TABLE "UserApiPermission" ADD COLUMN "Level" varchar(8) NULL;
ALTER TABLE "UserApiPermission" DISABLE TRIGGER "TR_UserApiPermission_Immutable";
UPDATE "UserApiPermission" SET "Level" = CASE WHEN "CanIssueTokens" THEN 'publish' ELSE 'none' END;
ALTER TABLE "UserApiPermission" ENABLE TRIGGER "TR_UserApiPermission_Immutable";
ALTER TABLE "UserApiPermission" ALTER COLUMN "Level" SET NOT NULL,
    ADD CONSTRAINT "CK_UserApiPermission_Level" CHECK ("Level" IN ('none','read','draft','publish')),
    ADD CONSTRAINT "CK_UserApiPermission_LevelIssue" CHECK ("CanIssueTokens" = ("Level" <> 'none'));

-- What the security overview reports until a WikiAdmin confirms the review: only when some account could issue tokens, so a fresh
-- installation shows nothing. Counted before the scopes below change.
CREATE TABLE "ApiLevelsReview" (
    "Id" uuid PRIMARY KEY,
    "CreatedAt" timestamptz NOT NULL DEFAULT clock_timestamp(),
    "Accounts" integer NOT NULL CHECK ("Accounts" >= 0),
    "Tokens" integer NOT NULL CHECK ("Tokens" >= 0),
    "ConfirmedById" uuid NULL REFERENCES "UserAccount"("Id"),
    "ConfirmedAt" timestamptz NULL,
    CONSTRAINT "CK_ApiLevelsReview_Confirmation" CHECK (("ConfirmedById" IS NULL) = ("ConfirmedAt" IS NULL))
);
INSERT INTO "ApiLevelsReview"("Id","Accounts","Tokens")
SELECT gen_random_uuid(), counts.accounts, counts.tokens
FROM (SELECT
        (SELECT count(*) FROM "UserApiPermission" WHERE "ActiveTo" IS NULL AND "Level" <> 'none')::integer AS accounts,
        (SELECT count(*) FROM "PersonalApiTokenScope" WHERE "Scope" = 'write')::integer AS tokens) counts
WHERE counts.accounts > 0;

-- Scopes: write → draft + publish. The inline CHECK of 008 has a generated name, so it is looked up.
DO $$
DECLARE constraint_name text;
BEGIN
    FOR constraint_name IN SELECT conname FROM pg_constraint
        WHERE conrelid = '"PersonalApiTokenScope"'::regclass AND contype = 'c'
    LOOP
        EXECUTE format('ALTER TABLE "PersonalApiTokenScope" DROP CONSTRAINT %I', constraint_name);
    END LOOP;
END $$;
INSERT INTO "PersonalApiTokenScope"("TokenId","Scope") SELECT "TokenId",'draft' FROM "PersonalApiTokenScope" WHERE "Scope"='write';
INSERT INTO "PersonalApiTokenScope"("TokenId","Scope") SELECT "TokenId",'publish' FROM "PersonalApiTokenScope" WHERE "Scope"='write';
DELETE FROM "PersonalApiTokenScope" WHERE "Scope"='write';
ALTER TABLE "PersonalApiTokenScope" ADD CONSTRAINT "CK_PersonalApiTokenScope_Scope" CHECK ("Scope" IN ('read','draft','publish'));

-- Channels: every existing token keeps working on the REST API it was issued for.
CREATE TABLE "PersonalApiTokenChannel" (
    "TokenId" uuid NOT NULL REFERENCES "PersonalApiToken"("Id"),
    "Channel" varchar(8) NOT NULL CONSTRAINT "CK_PersonalApiTokenChannel_Channel" CHECK ("Channel" IN ('rest','mcp')),
    PRIMARY KEY ("TokenId","Channel")
);
INSERT INTO "PersonalApiTokenChannel"("TokenId","Channel") SELECT "Id",'rest' FROM "PersonalApiToken";

-- Audit: none → level is enabled, level → none disabled, a change between two levels is the new action changed. The previous
-- interval is read as atlas_audit_account_revision reads it.
CREATE OR REPLACE FUNCTION atlas_audit_api_permission() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE previous_level varchar(8);
BEGIN
    IF NEW."ActiveTo" IS NOT NULL THEN RETURN NEW; END IF;
    SELECT "Level" INTO previous_level FROM "UserApiPermission"
        WHERE "UserId"=NEW."UserId" AND "Id"<>NEW."Id" ORDER BY "ActiveFrom" DESC LIMIT 1;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","ApiPermissionId")
    VALUES(NEW."ChangedById",
        CASE WHEN NEW."Level"='none' THEN 'api_permission.disabled'
             WHEN COALESCE(previous_level,'none')='none' THEN 'api_permission.enabled'
             ELSE 'api_permission.changed' END,
        NEW."Id");
    RETURN NEW;
END $$;

-- Full definitions as of 2026100104 plus api_permission.changed and the ApiLevel column at the end (T3 redefines both after this).
ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_ActionTarget";
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_ActionTarget" CHECK (
    ("AccountRevisionId" IS NOT NULL AND "Action" IN ('account.created','account.updated','credential.reset_requested','credential.password_changed','credential.activated','credential.recovered')) OR
    ("GroupRevisionId" IS NOT NULL AND "Action" IN ('group.created','group.renamed')) OR
    ("GroupMemberId" IS NOT NULL AND "Action" IN ('membership.added','membership.removed')) OR
    ("SpaceGrantId" IS NOT NULL AND "Action" IN ('grant.added','grant.removed')) OR
    ("ApiPermissionId" IS NOT NULL AND "Action" IN ('api_permission.enabled','api_permission.disabled','api_permission.changed')) OR
    ("ResourceGrantId" IS NOT NULL AND "Action" IN ('resource_grant.added','resource_grant.removed')) OR
    ("ProtectionSettingId" IS NOT NULL AND "Action" IN ('protection.set','protection.cleared')) OR
    ("TrustedAddressRevisionId" IS NOT NULL AND "Action" IN ('trusted_network.created','trusted_network.updated','trusted_network.removed',
        'trusted_proxy.created','trusted_proxy.updated','trusted_proxy.removed')) OR
    ("SecurityRevisionId" IS NOT NULL AND "Action" = 'security.proxy_hops_changed') OR
    ("ReauthChallengeId" IS NOT NULL AND "Action" IN ('reauth.consumed','reauth.window_used')) OR
    ("SecretReleaseEventId" IS NOT NULL AND "Action" IN ('secret.released','secret.release_denied','secret.client_report')) OR
    ("AccessRequestId" IS NOT NULL AND "Action" IN ('access_request.created','access_request.approved','access_request.rejected','access_request.cancelled')) OR
    ("HubChangeId" IS NOT NULL AND "Action" IN ('hub.connection.changed','hub.binding.changed','hub.identity.changed')));

CREATE OR REPLACE VIEW "AccessAuditView" AS
SELECT e."Id",e."ActorId",CAST(COALESCE(actor."DisplayName",CASE WHEN hub."Id" IS NOT NULL OR bound."GroupId" IS NOT NULL THEN 'Synchronizace Hubu' END) AS varchar(200)) AS "ActorName",e."OccurredAt",e."Action",
    COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId",request."RequesterId",hub."UserId") AS "UserId",
    COALESCE(account."DisplayName",subject."DisplayName") AS "UserName",
    COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId",hub."GroupId") AS "GroupId",
    COALESCE(group_revision."Name",user_group."Name") AS "GroupName",
    COALESCE(grant_row."SpaceId",page."SpaceId",resource."VaultSpaceId",protected_vault."SpaceId",release."SpaceId",request."VaultSpaceId") AS "SpaceId",space."Name" AS "SpaceName",
    COALESCE(grant_row."Role",resource."Role",request."Level") AS "Role",
    account."IsActive",account."IsWikiAdmin",permission."CanIssueTokens",
    e."AccountRevisionId",e."GroupRevisionId",e."GroupMemberId",e."SpaceGrantId",e."ApiPermissionId",e."ResourceGrantId",
    resource."PageId",
    COALESCE(resource."VaultId",protection."VaultId",release."VaultId",request."VaultId") AS "VaultId",
    COALESCE(resource."SecretId",protection."SecretId",release."SecretId",request."SecretId") AS "SecretId",
    e."ProtectionSettingId",protection."Mode" AS "ProtectionMode",e."TrustedAddressRevisionId",address."Name" AS "TrustedAddressName",
    address."Range" AS "TrustedAddressRange",e."SecurityRevisionId",e."ReauthChallengeId",
    COALESCE(protection."ConfigurationVersion",address."ConfigurationVersion",security_revision."Version") AS "ConfigurationVersion",
    e."SecretReleaseEventId",e."AccessRequestId",
    release."Result",release."Field",release."Operation",release."Mode",release."ModeSource",release."MatchedNetwork",release."ReauthUsed",
    release."SessionId",release."RequestId",release."ClientIp",
    CASE WHEN e."Action" = 'access_request.created' THEN request."Reason"
         WHEN e."AccessRequestId" IS NOT NULL THEN request."DecisionNote" END AS "Note",
    target_vault."Name" AS "VaultName",target_secret."Title" AS "SecretTitle",
    COALESCE(hub."ConnectionId",bound."ConnectionId") AS "HubConnectionId",
    COALESCE(hub_binding."ExternalGroupId",bound."ExternalGroupId") AS "ExternalGroupId",
    COALESCE(hub."ExternalUserId",historical_identity."Subject") AS "ExternalUserId",
    hub."Reason" AS "HubReason",cast(hub_connection."Mode" AS integer) AS "HubMode",
    hub_binding."Enabled" AS "HubBindingEnabled",hub_binding."GrantsWikiAdmin" AS "HubGrantsWikiAdmin",
    cast(hub."PreviousState" AS integer) AS "HubPreviousState",cast(hub."CurrentState" AS integer) AS "HubCurrentState",
    permission."Level" AS "ApiLevel"
FROM "AccessAuditEvent" e
LEFT JOIN "HubChange" hub ON hub."Id"=e."HubChangeId"
LEFT JOIN "HubConnectionRevision" hub_connection ON hub_connection."Id"=hub."ConnectionRevisionId"
LEFT JOIN "HubGroupBindingRevision" hub_binding ON hub_binding."Id"=hub."BindingRevisionId"
LEFT JOIN "UserAccount" actor ON actor."Id"=e."ActorId"
LEFT JOIN "UserAccountRevision" account ON account."Id"=e."AccountRevisionId"
LEFT JOIN "UserGroupRevision" group_revision ON group_revision."Id"=e."GroupRevisionId"
LEFT JOIN "GroupMember" member ON member."Id"=e."GroupMemberId"
LEFT JOIN "SpaceGrant" grant_row ON grant_row."Id"=e."SpaceGrantId"
LEFT JOIN "UserApiPermission" permission ON permission."Id"=e."ApiPermissionId"
LEFT JOIN "ResourceGrant" resource ON resource."Id"=e."ResourceGrantId"
LEFT JOIN "Page" page ON page."Id"=resource."PageId"
LEFT JOIN "ProtectionSetting" protection ON protection."Id"=e."ProtectionSettingId"
LEFT JOIN "Vault" protected_vault ON protected_vault."Id"=protection."VaultId"
LEFT JOIN "TrustedAddressRevision" address ON address."Id"=e."TrustedAddressRevisionId"
LEFT JOIN "SecurityConfigurationRevision" security_revision ON security_revision."Id"=e."SecurityRevisionId"
LEFT JOIN "SecretReleaseEvent" release ON release."Id"=e."SecretReleaseEventId"
LEFT JOIN "VaultAccessRequest" request ON request."Id"=e."AccessRequestId"
LEFT JOIN "UserAccount" subject ON subject."Id"=COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId",request."RequesterId",hub."UserId")
LEFT JOIN "UserGroup" user_group ON user_group."Id"=COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId",hub."GroupId")
LEFT JOIN "Space" space ON space."Id"=COALESCE(grant_row."SpaceId",page."SpaceId",resource."VaultSpaceId",protected_vault."SpaceId",release."SpaceId",request."VaultSpaceId")
LEFT JOIN "Vault" target_vault ON target_vault."Id"=COALESCE(resource."VaultId",protection."VaultId",release."VaultId",request."VaultId")
LEFT JOIN "VaultSecret" target_secret ON target_secret."Id"=COALESCE(resource."SecretId",protection."SecretId",release."SecretId",request."SecretId")
-- The binding identity never changes. Only events after its first revision are externally managed.
LEFT JOIN "HubGroupBinding" bound ON bound."GroupId"=COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId")
    AND EXISTS(SELECT 1 FROM "HubGroupBindingRevision" br WHERE br."GroupId"=bound."GroupId" AND br."ActiveFrom"<=e."OccurredAt")
LEFT JOIN LATERAL (
    SELECT identity."Subject" FROM "ExternalIdentity" identity
    WHERE identity."UserId"=member."UserId" AND identity."ActiveFrom"<=e."OccurredAt"
        AND (identity."ActiveTo" IS NULL OR e."OccurredAt"<identity."ActiveTo")
        AND EXISTS(SELECT 1 FROM "HubConnectionRevision" c WHERE c."ConnectionId"=bound."ConnectionId" AND c."Issuer"=identity."Issuer")
    ORDER BY identity."ActiveFrom" DESC,identity."Id" LIMIT 1
) historical_identity ON true;
