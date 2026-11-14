-- Audit of writes over the personal API and MCP (MCP plan T3), and the MCP module switch the C1 module reads.
-- A fact per write attempt, refused ones included: who, which token, channel, operation, target ids, outcome code, resulting version and
-- request id. Never content, a page title or an error text. The caller writes it in its own short transaction after the result, so a
-- refusal whose handler rolled back is still recorded. Retention as the rest of the access audit: none (plan O6).

-- The audit actor is always the token's owner: the pair is a foreign key.
ALTER TABLE "PersonalApiToken" ADD CONSTRAINT "UX_PersonalApiToken_IdUser" UNIQUE ("Id","UserId");

CREATE TABLE "ApiActivity" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY CONSTRAINT "PK_ApiActivity" PRIMARY KEY,
    "OccurredAt" timestamptz NOT NULL DEFAULT clock_timestamp(),
    "ActorId" uuid NOT NULL CONSTRAINT "FK_ApiActivity_Actor" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    "TokenId" uuid NOT NULL,
    "Channel" varchar(8) NOT NULL CONSTRAINT "CK_ApiActivity_Channel" CHECK ("Channel" IN ('api','mcp')),
    -- REST: page.create, page.save, …; MCP: the tool name (atlas_update_section, atlas_publish, …).
    "Operation" varchar(40) NOT NULL CONSTRAINT "CK_ApiActivity_Operation" CHECK ("Operation" ~ '^[a-z_.]{3,40}$'),
    -- No foreign key: a refusal (404) may name a page that does not exist. The view names only a published page.
    "PageId" uuid NULL,
    "SpaceId" uuid NULL,
    -- ok, or the refusal code: not_found, scope_missing, version_conflict, collaboration_unsaved, invalid, …
    "Outcome" varchar(32) NOT NULL CONSTRAINT "CK_ApiActivity_Outcome" CHECK ("Outcome" ~ '^[a-z_]{2,32}$'),
    "ResultVersion" integer NULL,
    "RequestId" varchar(64) NULL,
    CONSTRAINT "FK_ApiActivity_Token" FOREIGN KEY ("TokenId","ActorId") REFERENCES "PersonalApiToken"("Id","UserId") ON DELETE RESTRICT
);
CREATE INDEX "IX_ApiActivity_Token" ON "ApiActivity"("TokenId","Id" DESC);
CREATE TRIGGER "TR_ApiActivity_Immutable" BEFORE UPDATE OR DELETE ON "ApiActivity"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_immutable();

-- The MCP module: temporal like EditorAssistSetting. No row means off (plan O2: off after the upgrade).
CREATE TABLE "McpSetting" (
    "Id" uuid CONSTRAINT "PK_McpSetting" PRIMARY KEY,
    "Enabled" boolean NOT NULL,
    "ChangedById" uuid NOT NULL CONSTRAINT "FK_McpSetting_ChangedBy" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz,
    CONSTRAINT "CK_McpSetting_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_McpSetting_Interval" EXCLUDE USING gist (tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_McpSetting_Current" ON "McpSetting"((true)) WHERE "ActiveTo" IS NULL;
CREATE TRIGGER "TR_McpSetting_Immutable" BEFORE UPDATE OR DELETE ON "McpSetting"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();

ALTER TABLE "AccessAuditEvent"
    ADD COLUMN "ApiActivityId" bigint NULL CONSTRAINT "FK_AccessAuditEvent_ApiActivity" REFERENCES "ApiActivity"("Id") ON DELETE RESTRICT,
    ADD COLUMN "McpSettingId" uuid NULL CONSTRAINT "FK_AccessAuditEvent_McpSetting" REFERENCES "McpSetting"("Id") ON DELETE RESTRICT;
CREATE UNIQUE INDEX "UX_AccessAuditEvent_ApiActivity" ON "AccessAuditEvent"("ApiActivityId") WHERE "ApiActivityId" IS NOT NULL;
CREATE UNIQUE INDEX "UX_AccessAuditEvent_McpSetting" ON "AccessAuditEvent"("McpSettingId") WHERE "McpSettingId" IS NOT NULL;

-- Full definitions: 2026100104 (one target) and 2026100600 (action per target) plus the two new targets.
ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_OneTarget";
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_OneTarget" CHECK
    (num_nonnulls("AccountRevisionId","GroupRevisionId","GroupMemberId","SpaceGrantId","ApiPermissionId","ResourceGrantId",
        "ProtectionSettingId","TrustedAddressRevisionId","SecurityRevisionId","ReauthChallengeId",
        "SecretReleaseEventId","AccessRequestId","HubChangeId","ApiActivityId","McpSettingId")=1);
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
    ("McpSettingId" IS NOT NULL AND "Action" IN ('mcp.enabled','mcp.disabled')));

-- Every activity row has its event in the same statement: api.write over /api/v1, mcp.publish for the publishing tool, mcp.write for
-- every other MCP write tool.
CREATE FUNCTION atlas_audit_api_activity() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO "AccessAuditEvent"("ActorId","Action","ApiActivityId")
    VALUES(NEW."ActorId",
        CASE WHEN NEW."Channel"='api' THEN 'api.write'
             WHEN NEW."Operation"='atlas_publish' THEN 'mcp.publish'
             ELSE 'mcp.write' END,
        NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_ApiActivity_Audit" AFTER INSERT ON "ApiActivity"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_api_activity();

-- A new current interval is a switch: closing the previous one (an UPDATE of ActiveTo) is not.
CREATE FUNCTION atlas_audit_mcp_setting() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW."ActiveTo" IS NOT NULL THEN RETURN NEW; END IF;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","McpSettingId")
    VALUES(NEW."ChangedById",CASE WHEN NEW."Enabled" THEN 'mcp.enabled' ELSE 'mcp.disabled' END,NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_McpSetting_Audit" AFTER INSERT ON "McpSetting"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_mcp_setting();

-- Every column of 2026100600 in the same order (ApiLevel last of them), then the activity columns. PageId and SpaceId also name the
-- target of an activity (SpaceId as recorded at the write), RequestId its request. PageTitle is only ever a published title.
CREATE OR REPLACE VIEW "AccessAuditView" AS
SELECT e."Id",e."ActorId",CAST(COALESCE(actor."DisplayName",CASE WHEN hub."Id" IS NOT NULL OR bound."GroupId" IS NOT NULL THEN 'Synchronizace Hubu' END) AS varchar(200)) AS "ActorName",e."OccurredAt",e."Action",
    COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId",request."RequesterId",hub."UserId") AS "UserId",
    COALESCE(account."DisplayName",subject."DisplayName") AS "UserName",
    COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId",hub."GroupId") AS "GroupId",
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
    e."McpSettingId"
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
LEFT JOIN "UserAccount" subject ON subject."Id"=COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId",request."RequesterId",hub."UserId")
LEFT JOIN "UserGroup" user_group ON user_group."Id"=COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId",hub."GroupId")
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
