-- Content channel assistant (Atlas AI agent spec D2, §4.3): the in-process assistant writes and opens pages as the signed-in user,
-- without a personal API token. Its ceiling is that user's web content rights (D19), so nothing here relates it to an API level.
-- 'assistant' has 9 characters: the Channel columns widen from varchar(8) to varchar(16). A longer varchar limit rewrites neither a
-- table nor an index, but PostgreSQL refuses to retype a column a view reads, so AccessAuditView (it reads ApiActivity."Channel")
-- is dropped first and recreated below exactly as 2026100850 defined it.
DROP VIEW "AccessAuditView";
ALTER TABLE "PageRevision" ALTER COLUMN "Channel" TYPE varchar(16);
ALTER TABLE "PagePublication" ALTER COLUMN "Channel" TYPE varchar(16);
ALTER TABLE "PageAccessEvent" ALTER COLUMN "Channel" TYPE varchar(16);
ALTER TABLE "ApiActivity" ALTER COLUMN "Channel" TYPE varchar(16);

-- Full definitions: 2026100610 plus assistant, which like web carries no token.
ALTER TABLE "PageRevision" DROP CONSTRAINT "CK_PageRevision_Channel";
ALTER TABLE "PageRevision" ADD CONSTRAINT "CK_PageRevision_Channel"
  CHECK ("Channel" IN ('web','api','mcp','assistant','import','system','legacy') AND (("Channel" IN ('api','mcp')) = ("ApiTokenId" IS NOT NULL)));
ALTER TABLE "PagePublication" DROP CONSTRAINT "CK_PagePublication_Channel";
ALTER TABLE "PagePublication" ADD CONSTRAINT "CK_PagePublication_Channel"
  CHECK ("Channel" IN ('web','api','mcp','assistant','import','system','legacy') AND (("Channel" IN ('api','mcp')) = ("ApiTokenId" IS NOT NULL)));
ALTER TABLE "PageAccessEvent" DROP CONSTRAINT "CK_PageAccessEvent_Channel";
ALTER TABLE "PageAccessEvent" ADD CONSTRAINT "CK_PageAccessEvent_Channel" CHECK (
  ("Channel" IS NULL AND "ApiTokenId" IS NULL) OR
  ("Action" = 'page.opened' AND "Channel" IN ('web','api','mcp','assistant','system') AND (("Channel" IN ('api','mcp')) = ("ApiTokenId" IS NOT NULL))));

-- An assistant write is audited like an MCP one, as its user and without a token; every other row keeps its token. The composite
-- foreign key (TokenId, ActorId) skips a row whose TokenId is NULL; ActorId keeps its own foreign key.
ALTER TABLE "ApiActivity" ALTER COLUMN "TokenId" DROP NOT NULL;
ALTER TABLE "ApiActivity" DROP CONSTRAINT "CK_ApiActivity_Channel";
ALTER TABLE "ApiActivity" ADD CONSTRAINT "CK_ApiActivity_Channel"
  CHECK ("Channel" IN ('api','mcp','assistant') AND (("Channel" = 'assistant') = ("TokenId" IS NULL)));

-- Full definition: 2026100850 plus assistant.write and assistant.publish.
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
    ("HubChangeId" IS NOT NULL AND "Action" IN ('hub.connection.changed','hub.binding.changed','hub.identity.changed')) OR
    ("ApiActivityId" IS NOT NULL AND "Action" IN ('api.write','mcp.write','mcp.publish','assistant.write','assistant.publish')) OR
    ("McpSettingId" IS NOT NULL AND "Action" IN ('mcp.enabled','mcp.disabled')) OR
    ("GroupAiPolicyId" IS NOT NULL AND "Action" = 'ai.group_policy.changed') OR
    ("UserAiPolicyId" IS NOT NULL AND "Action" = 'ai.user_policy.changed'));

-- 2026100620 plus the assistant: assistant.publish for the publishing tool, assistant.write for every other tool.
CREATE OR REPLACE FUNCTION atlas_audit_api_activity() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO "AccessAuditEvent"("ActorId","Action","ApiActivityId")
    VALUES(NEW."ActorId",
        CASE WHEN NEW."Channel"='api' THEN 'api.write'
             WHEN NEW."Channel"='assistant' THEN CASE WHEN NEW."Operation"='atlas_publish' THEN 'assistant.publish' ELSE 'assistant.write' END
             WHEN NEW."Operation"='atlas_publish' THEN 'mcp.publish'
             ELSE 'mcp.write' END,
        NEW."Id");
    RETURN NEW;
END $$;

-- Unchanged from 2026100850; ApiTokenId and ApiTokenName are NULL for an assistant row.
CREATE VIEW "AccessAuditView" AS
SELECT e."Id",e."ActorId",CAST(COALESCE(actor."DisplayName",CASE WHEN hub."Id" IS NOT NULL OR bound."GroupId" IS NOT NULL THEN 'Synchronizace Hubu' END) AS varchar(200)) AS "ActorName",e."OccurredAt",e."Action",
    COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId",request."RequesterId",hub."UserId",ai_user."UserId") AS "UserId",
    COALESCE(account."DisplayName",subject."DisplayName") AS "UserName",
    COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId",hub."GroupId",ai_group."GroupId") AS "GroupId",
    COALESCE(group_revision."Name",user_group."Name") AS "GroupName",
    COALESCE(grant_row."SpaceId",activity."SpaceId",page."SpaceId",resource."VaultSpaceId",protected_vault."SpaceId",release."SpaceId",request."VaultSpaceId") AS "SpaceId",space."Name" AS "SpaceName",
    COALESCE(grant_row."Role",resource."Role",request."Level") AS "Role",
    account."IsActive",account."IsWikiAdmin",permission."CanIssueTokens",
    e."AccountRevisionId",e."GroupRevisionId",e."GroupMemberId",e."SpaceGrantId",e."ApiPermissionId",e."ResourceGrantId",
    COALESCE(resource."PageId",activity."PageId") AS "PageId",
    COALESCE(resource."VaultId",protection."VaultId",release."VaultId",request."VaultId") AS "VaultId",
    COALESCE(resource."SecretId",protection."SecretId",release."SecretId",request."SecretId") AS "SecretId",
    e."ProtectionSettingId",protection."Mode" AS "ProtectionMode",e."TrustedAddressRevisionId",address."Name" AS "TrustedAddressName",
    address."Range" AS "TrustedAddressRange",e."SecurityRevisionId",e."ReauthChallengeId",
    COALESCE(protection."ConfigurationVersion",address."ConfigurationVersion",security_revision."Version") AS "ConfigurationVersion",
    e."SecretReleaseEventId",e."AccessRequestId",
    release."Result",release."Field",release."Operation",release."Mode",release."ModeSource",release."MatchedNetwork",release."ReauthUsed",
    release."SessionId",CAST(COALESCE(release."RequestId",activity."RequestId") AS varchar(128)) AS "RequestId",release."ClientIp",
    CASE WHEN e."Action" = 'access_request.created' THEN request."Reason"
         WHEN e."AccessRequestId" IS NOT NULL THEN request."DecisionNote" END AS "Note",
    target_vault."Name" AS "VaultName",target_secret."Title" AS "SecretTitle",
    COALESCE(hub."ConnectionId",bound."ConnectionId") AS "HubConnectionId",
    COALESCE(hub_binding."ExternalGroupId",bound."ExternalGroupId") AS "ExternalGroupId",
    COALESCE(hub."ExternalUserId",historical_identity."Subject") AS "ExternalUserId",
    hub."Reason" AS "HubReason",cast(hub_connection."Mode" AS integer) AS "HubMode",
    hub_binding."Enabled" AS "HubBindingEnabled",hub_binding."GrantsWikiAdmin" AS "HubGrantsWikiAdmin",
    cast(hub."PreviousState" AS integer) AS "HubPreviousState",cast(hub."CurrentState" AS integer) AS "HubCurrentState",
    permission."Level" AS "ApiLevel",
    e."ApiActivityId",activity."Channel" AS "ApiChannel",activity."Operation" AS "ApiOperation",activity."Outcome" AS "ApiOutcome",
    activity."ResultVersion" AS "ApiResultVersion",activity."TokenId" AS "ApiTokenId",token."Name" AS "ApiTokenName",
    published."Title" AS "PageTitle",
    e."McpSettingId",e."GroupAiPolicyId",e."UserAiPolicyId"
FROM "AccessAuditEvent" e
LEFT JOIN "GroupAiPolicy" ai_group ON ai_group."Id"=e."GroupAiPolicyId"
LEFT JOIN "UserAiPolicy" ai_user ON ai_user."Id"=e."UserAiPolicyId"
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
LEFT JOIN "ApiActivity" activity ON activity."Id"=e."ApiActivityId"
LEFT JOIN "PersonalApiToken" token ON token."Id"=activity."TokenId"
LEFT JOIN "Page" page ON page."Id"=COALESCE(resource."PageId",activity."PageId")
LEFT JOIN "PageRevision" published ON published."Id"=page."PublishedRevisionId"
LEFT JOIN "ProtectionSetting" protection ON protection."Id"=e."ProtectionSettingId"
LEFT JOIN "Vault" protected_vault ON protected_vault."Id"=protection."VaultId"
LEFT JOIN "TrustedAddressRevision" address ON address."Id"=e."TrustedAddressRevisionId"
LEFT JOIN "SecurityConfigurationRevision" security_revision ON security_revision."Id"=e."SecurityRevisionId"
LEFT JOIN "SecretReleaseEvent" release ON release."Id"=e."SecretReleaseEventId"
LEFT JOIN "VaultAccessRequest" request ON request."Id"=e."AccessRequestId"
LEFT JOIN "UserAccount" subject ON subject."Id"=COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId",request."RequesterId",hub."UserId",ai_user."UserId")
LEFT JOIN "UserGroup" user_group ON user_group."Id"=COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId",hub."GroupId",ai_group."GroupId")
LEFT JOIN "Space" space ON space."Id"=COALESCE(grant_row."SpaceId",activity."SpaceId",page."SpaceId",resource."VaultSpaceId",protected_vault."SpaceId",release."SpaceId",request."VaultSpaceId")
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
