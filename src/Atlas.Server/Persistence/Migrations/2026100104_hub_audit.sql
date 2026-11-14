-- Forward-only audit coverage: no actor or observation time is invented for existing configuration.
-- Exact revision ownership/subject below are constrained projections, populated once by the source triggers.
-- Revision references intentionally pin the historical version, not the current configuration.
ALTER TABLE "HubConnectionRevision" ADD CONSTRAINT "UX_HubConnectionRevision_IdConnection" UNIQUE ("Id","ConnectionId");
ALTER TABLE "HubGroupBindingRevision" ADD CONSTRAINT "UX_HubGroupBindingRevision_IdGroupConnection" UNIQUE ("Id","GroupId","ConnectionId");
ALTER TABLE "ExternalIdentity" ADD CONSTRAINT "UX_ExternalIdentity_IdUserSubject" UNIQUE ("Id","UserId","Subject");

CREATE TABLE "HubChange" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY CONSTRAINT "PK_HubChange" PRIMARY KEY,
    "ConnectionId" uuid NOT NULL,
    "GroupId" uuid NULL,
    "UserId" uuid NULL,
    "ExternalIdentityId" uuid NULL,
    "ExternalUserId" text NULL,
    "ConnectionRevisionId" uuid NULL,
    "BindingRevisionId" uuid NULL,
    "PreviousState" smallint NULL,
    "CurrentState" smallint NULL,
    "Reason" varchar(32) NOT NULL,
    CONSTRAINT "FK_HubChange_Connection" FOREIGN KEY ("ConnectionId") REFERENCES "HubConnection"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_HubChange_Group" FOREIGN KEY ("GroupId") REFERENCES "UserGroup"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_HubChange_User" FOREIGN KEY ("UserId") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_HubChange_Identity" FOREIGN KEY ("ExternalIdentityId","UserId","ExternalUserId") REFERENCES "ExternalIdentity"("Id","UserId","Subject") ON DELETE RESTRICT,
    CONSTRAINT "FK_HubChange_ConnectionRevision" FOREIGN KEY ("ConnectionRevisionId","ConnectionId") REFERENCES "HubConnectionRevision"("Id","ConnectionId") ON DELETE RESTRICT,
    CONSTRAINT "FK_HubChange_BindingRevision" FOREIGN KEY ("BindingRevisionId","GroupId","ConnectionId") REFERENCES "HubGroupBindingRevision"("Id","GroupId","ConnectionId") ON DELETE RESTRICT,
    CONSTRAINT "CK_HubChange_Category" CHECK (
        ("ConnectionRevisionId" IS NOT NULL AND "Reason" IN ('connection.created','connection.updated') AND
            num_nonnulls("GroupId","UserId","ExternalIdentityId","ExternalUserId","BindingRevisionId","PreviousState","CurrentState")=0) OR
        ("BindingRevisionId" IS NOT NULL AND "GroupId" IS NOT NULL AND "Reason" IN ('binding.attached','binding.updated') AND
            num_nonnulls("UserId","ExternalIdentityId","ExternalUserId","ConnectionRevisionId","PreviousState","CurrentState")=0) OR
        ("ExternalIdentityId" IS NOT NULL AND "UserId" IS NOT NULL AND "ExternalUserId" IS NOT NULL AND "CurrentState" IS NOT NULL AND
            "Reason"='identity.observed' AND num_nonnulls("GroupId","ConnectionRevisionId","BindingRevisionId")=0)),
    CONSTRAINT "CK_HubChange_State" CHECK (("PreviousState" IS NULL OR "PreviousState" IN (0,1,2,3)) AND
        ("CurrentState" IS NULL OR "CurrentState" IN (0,1,2,3)) AND ("PreviousState" IS NULL OR "PreviousState"<>"CurrentState"))
);
CREATE INDEX "IX_HubChange_Connection" ON "HubChange"("ConnectionId","Id");
CREATE INDEX "IX_HubChange_Group" ON "HubChange"("GroupId") WHERE "GroupId" IS NOT NULL;
CREATE INDEX "IX_HubChange_User" ON "HubChange"("UserId") WHERE "UserId" IS NOT NULL;
CREATE INDEX "IX_HubChange_Identity" ON "HubChange"("ExternalIdentityId","UserId","ExternalUserId") WHERE "ExternalIdentityId" IS NOT NULL;
CREATE UNIQUE INDEX "UX_HubChange_ConnectionRevision" ON "HubChange"("ConnectionRevisionId") WHERE "ConnectionRevisionId" IS NOT NULL;
CREATE UNIQUE INDEX "UX_HubChange_BindingRevision" ON "HubChange"("BindingRevisionId") WHERE "BindingRevisionId" IS NOT NULL;
CREATE TRIGGER "TR_HubChange_Immutable" BEFORE UPDATE OR DELETE ON "HubChange"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_immutable();

ALTER TABLE "AccessAuditEvent" ADD COLUMN "HubChangeId" bigint NULL
    CONSTRAINT "FK_AccessAuditEvent_HubChange" REFERENCES "HubChange"("Id") ON DELETE RESTRICT;
CREATE UNIQUE INDEX "UX_AccessAuditEvent_HubChange" ON "AccessAuditEvent"("HubChangeId") WHERE "HubChangeId" IS NOT NULL;
ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_OneTarget";
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_OneTarget" CHECK
    (num_nonnulls("AccountRevisionId","GroupRevisionId","GroupMemberId","SpaceGrantId","ApiPermissionId","ResourceGrantId",
        "ProtectionSettingId","TrustedAddressRevisionId","SecurityRevisionId","ReauthChallengeId",
        "SecretReleaseEventId","AccessRequestId","HubChangeId")=1);

CREATE FUNCTION atlas_audit_hub_connection() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE fact bigint; reason text;
BEGIN
    reason := CASE WHEN EXISTS(SELECT 1 FROM "HubConnectionRevision" WHERE "ConnectionId"=NEW."ConnectionId" AND "Id"<>NEW."Id")
        THEN 'connection.updated' ELSE 'connection.created' END;
    INSERT INTO "HubChange"("ConnectionId","ConnectionRevisionId","Reason")
        VALUES(NEW."ConnectionId",NEW."Id",reason) RETURNING "Id" INTO fact;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","HubChangeId") VALUES(NEW."ChangedById",'hub.connection.changed',fact);
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_HubConnectionRevision_Audit" AFTER INSERT ON "HubConnectionRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_hub_connection();

CREATE FUNCTION atlas_audit_hub_binding() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE fact bigint; reason text;
BEGIN
    reason := CASE WHEN EXISTS(SELECT 1 FROM "HubGroupBindingRevision" WHERE "GroupId"=NEW."GroupId" AND "Id"<>NEW."Id")
        THEN 'binding.updated' ELSE 'binding.attached' END;
    INSERT INTO "HubChange"("ConnectionId","GroupId","BindingRevisionId","Reason")
        VALUES(NEW."ConnectionId",NEW."GroupId",NEW."Id",reason) RETURNING "Id" INTO fact;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","HubChangeId") VALUES(NEW."ChangedBy",'hub.binding.changed',fact);
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_HubGroupBindingRevision_Audit" AFTER INSERT ON "HubGroupBindingRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_hub_binding();

CREATE FUNCTION atlas_audit_hub_identity() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE fact bigint; previous smallint;
BEGIN
    IF TG_OP='INSERT' AND NEW."UpstreamAccountState"=0 THEN RETURN NEW; END IF;
    IF TG_OP='UPDATE' THEN
        IF NEW."UpstreamAccountState"=OLD."UpstreamAccountState" THEN RETURN NEW; END IF;
        previous := OLD."UpstreamAccountState";
    END IF;
    INSERT INTO "HubChange"("ConnectionId","UserId","ExternalIdentityId","ExternalUserId","PreviousState","CurrentState","Reason")
        VALUES(NEW."ConnectionId",NEW."UserId",NEW."ExternalIdentityId",NEW."ExternalUserId",previous,NEW."UpstreamAccountState",'identity.observed') RETURNING "Id" INTO fact;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","HubChangeId")
        VALUES(NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,'hub.identity.changed',fact);
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_HubIdentityState_Audit" AFTER INSERT OR UPDATE ON "HubIdentityState"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_hub_identity();

ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_ActionTarget";
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_ActionTarget" CHECK (
    ("AccountRevisionId" IS NOT NULL AND "Action" IN ('account.created','account.updated','credential.reset_requested','credential.password_changed','credential.activated','credential.recovered')) OR
    ("GroupRevisionId" IS NOT NULL AND "Action" IN ('group.created','group.renamed')) OR
    ("GroupMemberId" IS NOT NULL AND "Action" IN ('membership.added','membership.removed')) OR
    ("SpaceGrantId" IS NOT NULL AND "Action" IN ('grant.added','grant.removed')) OR
    ("ApiPermissionId" IS NOT NULL AND "Action" IN ('api_permission.enabled','api_permission.disabled')) OR
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
    cast(hub."PreviousState" AS integer) AS "HubPreviousState",cast(hub."CurrentState" AS integer) AS "HubCurrentState"
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
