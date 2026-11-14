CREATE TABLE "GroupAiPolicy" (
    "Id" uuid NOT NULL CONSTRAINT "PK_GroupAiPolicy" PRIMARY KEY,
    "GroupId" uuid NOT NULL CONSTRAINT "FK_GroupAiPolicy_Subject" REFERENCES "UserGroup"("Id") ON DELETE RESTRICT,
    "Enabled" boolean NOT NULL DEFAULT false,
    "SearchAnswerDailyLimit" integer NULL DEFAULT NULL CONSTRAINT "CK_GroupAiPolicy_SearchLimit" CHECK ("SearchAnswerDailyLimit" IS NULL OR "SearchAnswerDailyLimit" BETWEEN 0 AND 10000),
    "ChatAnswerDailyLimit" integer NULL DEFAULT NULL CONSTRAINT "CK_GroupAiPolicy_ChatLimit" CHECK ("ChatAnswerDailyLimit" IS NULL OR "ChatAnswerDailyLimit" BETWEEN 0 AND 10000),
    "PageSummaryDailyLimit" integer NULL DEFAULT NULL CONSTRAINT "CK_GroupAiPolicy_SummaryLimit" CHECK ("PageSummaryDailyLimit" IS NULL OR "PageSummaryDailyLimit" BETWEEN 0 AND 10000),
    "ChangedById" uuid NOT NULL CONSTRAINT "FK_GroupAiPolicy_ChangedBy" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "CK_GroupAiPolicy_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_GroupAiPolicy_Interval" EXCLUDE USING gist ("GroupId" WITH =, tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_GroupAiPolicy_Current" ON "GroupAiPolicy"("GroupId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_GroupAiPolicy_History" ON "GroupAiPolicy"("GroupId","ActiveFrom" DESC);
CREATE INDEX "IX_GroupAiPolicy_ChangedBy" ON "GroupAiPolicy"("ChangedById");
CREATE TRIGGER "TR_GroupAiPolicy_Immutable" BEFORE UPDATE OR DELETE ON "GroupAiPolicy"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();
ALTER TABLE "AccessAuditEvent" ADD COLUMN "GroupAiPolicyId" uuid NULL
    CONSTRAINT "FK_AccessAuditEvent_GroupAiPolicy" REFERENCES "GroupAiPolicy"("Id") ON DELETE RESTRICT;
CREATE UNIQUE INDEX "UX_AccessAuditEvent_GroupAiPolicy" ON "AccessAuditEvent"("GroupAiPolicyId") WHERE "GroupAiPolicyId" IS NOT NULL;

CREATE FUNCTION atlas_audit_group_ai_policy() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW."ActiveTo" IS NOT NULL THEN RETURN NEW; END IF;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","GroupAiPolicyId")
    VALUES(NEW."ChangedById",'ai.group_policy.changed',NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_GroupAiPolicy_Audit" AFTER INSERT ON "GroupAiPolicy"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_group_ai_policy();

CREATE TABLE "UserAiPolicy" (
    "Id" uuid NOT NULL CONSTRAINT "PK_UserAiPolicy" PRIMARY KEY,
    "UserId" uuid NOT NULL CONSTRAINT "FK_UserAiPolicy_Subject" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    "Enabled" boolean NULL DEFAULT NULL,
    "SearchAnswerDailyLimit" integer NULL DEFAULT NULL CONSTRAINT "CK_UserAiPolicy_SearchLimit" CHECK ("SearchAnswerDailyLimit" IS NULL OR "SearchAnswerDailyLimit" BETWEEN 0 AND 10000),
    "ChatAnswerDailyLimit" integer NULL DEFAULT NULL CONSTRAINT "CK_UserAiPolicy_ChatLimit" CHECK ("ChatAnswerDailyLimit" IS NULL OR "ChatAnswerDailyLimit" BETWEEN 0 AND 10000),
    "PageSummaryDailyLimit" integer NULL DEFAULT NULL CONSTRAINT "CK_UserAiPolicy_SummaryLimit" CHECK ("PageSummaryDailyLimit" IS NULL OR "PageSummaryDailyLimit" BETWEEN 0 AND 10000),
    "ChangedById" uuid NOT NULL CONSTRAINT "FK_UserAiPolicy_ChangedBy" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "CK_UserAiPolicy_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_UserAiPolicy_Interval" EXCLUDE USING gist ("UserId" WITH =, tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_UserAiPolicy_Current" ON "UserAiPolicy"("UserId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_UserAiPolicy_History" ON "UserAiPolicy"("UserId","ActiveFrom" DESC);
CREATE INDEX "IX_UserAiPolicy_ChangedBy" ON "UserAiPolicy"("ChangedById");
CREATE TRIGGER "TR_UserAiPolicy_Immutable" BEFORE UPDATE OR DELETE ON "UserAiPolicy"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();
ALTER TABLE "AccessAuditEvent" ADD COLUMN "UserAiPolicyId" uuid NULL
    CONSTRAINT "FK_AccessAuditEvent_UserAiPolicy" REFERENCES "UserAiPolicy"("Id") ON DELETE RESTRICT;
CREATE UNIQUE INDEX "UX_AccessAuditEvent_UserAiPolicy" ON "AccessAuditEvent"("UserAiPolicyId") WHERE "UserAiPolicyId" IS NOT NULL;

CREATE FUNCTION atlas_audit_user_ai_policy() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW."ActiveTo" IS NOT NULL THEN RETURN NEW; END IF;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","UserAiPolicyId")
    VALUES(NEW."ChangedById",'ai.user_policy.changed',NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_UserAiPolicy_Audit" AFTER INSERT ON "UserAiPolicy"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_user_ai_policy();

ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_OneTarget";
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_OneTarget" CHECK
    (num_nonnulls("AccountRevisionId","GroupRevisionId","GroupMemberId","SpaceGrantId","ApiPermissionId","ResourceGrantId",
        "ProtectionSettingId","TrustedAddressRevisionId","SecurityRevisionId","ReauthChallengeId",
        "SecretReleaseEventId","AccessRequestId","HubChangeId","ApiActivityId","McpSettingId","GroupAiPolicyId","UserAiPolicyId")=1);
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
    ("ApiActivityId" IS NOT NULL AND "Action" IN ('api.write','mcp.write','mcp.publish')) OR
    ("McpSettingId" IS NOT NULL AND "Action" IN ('mcp.enabled','mcp.disabled')) OR
    ("GroupAiPolicyId" IS NOT NULL AND "Action" = 'ai.group_policy.changed') OR
    ("UserAiPolicyId" IS NOT NULL AND "Action" = 'ai.user_policy.changed'));

CREATE OR REPLACE VIEW "AccessAuditView" AS
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
