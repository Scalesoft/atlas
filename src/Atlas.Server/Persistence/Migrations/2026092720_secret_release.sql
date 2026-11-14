-- Access requests and audited release of secret values (WP4 V5). Additive and forward-only: audit facts must never disappear.
-- No value of a secret is stored anywhere: an audit row names the field and the operation, never the value.

-- A request of one account for Čte (Read, level 20) on a whole vault (SecretId NULL) or one of its secrets. The row is the current
-- state; it changes exactly once, from pending to approved, rejected or cancelled (TR_VaultAccessRequest_Immutable), and every
-- transition is an AccessAuditEvent with its actor (TR_VaultAccessRequest_Audit), so the history is append-only. An approval
-- points at the ResourceGrant it created (or the current grant that already covered it); a pending request grants nothing.
CREATE TABLE "VaultAccessRequest" (
    "Id" uuid PRIMARY KEY,
    "VaultId" uuid NOT NULL,
    "SecretId" uuid NULL,
    "VaultSpaceId" uuid NOT NULL,
    "RequesterId" uuid NOT NULL,
    "Level" integer NOT NULL,
    "Reason" varchar(500) NOT NULL,
    "State" varchar(16) NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT clock_timestamp(),
    "DecidedBy" uuid NULL,
    "DecidedAt" timestamptz NULL,
    "DecisionNote" varchar(500) NULL,
    "GrantId" uuid NULL,
    "GrantExpiresAt" timestamptz NULL,
    CONSTRAINT "FK_VaultAccessRequest_Vault" FOREIGN KEY ("VaultId","VaultSpaceId") REFERENCES "Vault"("Id","SpaceId") ON DELETE RESTRICT,
    CONSTRAINT "FK_VaultAccessRequest_Secret" FOREIGN KEY ("SecretId","VaultId") REFERENCES "VaultSecret"("Id","VaultId") ON DELETE RESTRICT,
    CONSTRAINT "FK_VaultAccessRequest_Requester" FOREIGN KEY ("RequesterId") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_VaultAccessRequest_DecidedBy" FOREIGN KEY ("DecidedBy") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_VaultAccessRequest_Grant" FOREIGN KEY ("GrantId") REFERENCES "ResourceGrant"("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_VaultAccessRequest_Level" CHECK ("Level" = 20),
    CONSTRAINT "CK_VaultAccessRequest_Reason" CHECK (length(btrim("Reason")) > 0),
    CONSTRAINT "CK_VaultAccessRequest_State" CHECK ("State" IN ('pending','approved','rejected','cancelled')),
    CONSTRAINT "CK_VaultAccessRequest_Decision" CHECK (("State" = 'pending') = ("DecidedBy" IS NULL AND "DecidedAt" IS NULL)),
    CONSTRAINT "CK_VaultAccessRequest_Grant" CHECK (("State" = 'approved') = ("GrantId" IS NOT NULL)),
    CONSTRAINT "CK_VaultAccessRequest_Time" CHECK (isfinite("CreatedAt") AND ("DecidedAt" IS NULL OR "DecidedAt" >= "CreatedAt"))
);
-- One pending request of an account per target: a repeated request returns the pending one (idempotent).
CREATE UNIQUE INDEX "UX_VaultAccessRequest_Pending" ON "VaultAccessRequest"
    ("RequesterId","VaultId",COALESCE("SecretId",'00000000-0000-0000-0000-000000000000'::uuid)) WHERE "State" = 'pending';
CREATE INDEX "IX_VaultAccessRequest_Vault" ON "VaultAccessRequest"("VaultId","CreatedAt" DESC);
CREATE INDEX "IX_VaultAccessRequest_Pending" ON "VaultAccessRequest"("CreatedAt" DESC) WHERE "State" = 'pending';
CREATE INDEX "IX_VaultAccessRequest_Requester" ON "VaultAccessRequest"("RequesterId","CreatedAt" DESC);
CREATE INDEX "IX_VaultAccessRequest_Secret" ON "VaultAccessRequest"("SecretId") WHERE "SecretId" IS NOT NULL;

CREATE FUNCTION atlas_access_request_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Access requests cannot be deleted' USING ERRCODE = '23514'; END IF;
    IF OLD."State" <> 'pending' OR NEW."State" = 'pending'
        OR (NEW."Id",NEW."VaultId",NEW."SecretId",NEW."VaultSpaceId",NEW."RequesterId",NEW."Level",NEW."Reason",NEW."CreatedAt")
            IS DISTINCT FROM (OLD."Id",OLD."VaultId",OLD."SecretId",OLD."VaultSpaceId",OLD."RequesterId",OLD."Level",OLD."Reason",OLD."CreatedAt") THEN
        RAISE EXCEPTION 'An access request is decided once and its request is immutable' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_VaultAccessRequest_Immutable" BEFORE UPDATE OR DELETE ON "VaultAccessRequest"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_request_guard();

-- One immutable fact per release decision or client report. Kind release: the server's decision on one request, Result released
-- (the value was returned after this row committed) or the reason it was not (forbidden: no Čte, not_found: the secret or its
-- vault is gone for the account, reauth_required, unsupported: the sign-in cannot prove a fresh login, unavailable: the vault
-- file cannot be read now). Kind client_report: the browser says it showed or copied a released value; it is not proof of
-- reading. Mode, ModeSource and MatchedNetwork are the IReleasePolicy decision (null when the request stopped before it).
-- ClientIp is the address the platform resolved (trusted proxies only); SessionId the atlas_session claim of the sign-in.
CREATE TABLE "SecretReleaseEvent" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY CONSTRAINT "PK_SecretReleaseEvent" PRIMARY KEY,
    "OccurredAt" timestamptz NOT NULL DEFAULT clock_timestamp(),
    "Kind" varchar(16) NOT NULL,
    "ActorId" uuid NOT NULL,
    "SecretId" uuid NOT NULL,
    "VaultId" uuid NOT NULL,
    "SpaceId" uuid NOT NULL,
    "Field" varchar(16) NOT NULL,
    "Operation" varchar(16) NOT NULL,
    "Result" varchar(24) NOT NULL,
    "Mode" varchar(32) NULL,
    "ModeSource" varchar(16) NULL,
    "MatchedNetwork" varchar(200) NULL,
    "ReauthUsed" boolean NOT NULL,
    "SessionId" varchar(64) NULL,
    "RequestId" varchar(128) NULL,
    "ClientIp" varchar(64) NULL,
    CONSTRAINT "FK_SecretReleaseEvent_Actor" FOREIGN KEY ("ActorId") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_SecretReleaseEvent_Secret" FOREIGN KEY ("SecretId","VaultId") REFERENCES "VaultSecret"("Id","VaultId") ON DELETE RESTRICT,
    CONSTRAINT "FK_SecretReleaseEvent_Vault" FOREIGN KEY ("VaultId","SpaceId") REFERENCES "Vault"("Id","SpaceId") ON DELETE RESTRICT,
    CONSTRAINT "CK_SecretReleaseEvent_Kind" CHECK ("Kind" IN ('release','client_report')),
    CONSTRAINT "CK_SecretReleaseEvent_Field" CHECK ("Field" IN ('userName','password','url','notes')),
    CONSTRAINT "CK_SecretReleaseEvent_Operation" CHECK ("Operation" IN ('reveal','copy')),
    CONSTRAINT "CK_SecretReleaseEvent_Result" CHECK (
        ("Kind" = 'release' AND "Result" IN ('released','forbidden','not_found','reauth_required','unsupported','unavailable')) OR
        ("Kind" = 'client_report' AND "Result" IN ('shown','copied'))),
    CONSTRAINT "CK_SecretReleaseEvent_Mode" CHECK ("Mode" IS NULL OR "Mode" IN ('never','outsideTrustedNetworks','always')),
    CONSTRAINT "CK_SecretReleaseEvent_ModeSource" CHECK ("ModeSource" IS NULL OR "ModeSource" IN ('instance','vault','secret')),
    CONSTRAINT "CK_SecretReleaseEvent_Reauth" CHECK (NOT "ReauthUsed" OR "Result" = 'released'),
    CONSTRAINT "CK_SecretReleaseEvent_Time" CHECK (isfinite("OccurredAt"))
);
CREATE INDEX "IX_SecretReleaseEvent_Secret" ON "SecretReleaseEvent"("SecretId","Id" DESC);
CREATE INDEX "IX_SecretReleaseEvent_Vault" ON "SecretReleaseEvent"("VaultId","Id" DESC);
CREATE INDEX "IX_SecretReleaseEvent_Actor" ON "SecretReleaseEvent"("ActorId","Id" DESC);
CREATE TRIGGER "TR_SecretReleaseEvent_Immutable" BEFORE UPDATE OR DELETE ON "SecretReleaseEvent"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_immutable();

-- Both facts join the one access audit feed (AccessAuditEvent), so the global audit and the per-target audit page by one keyset.
ALTER TABLE "AccessAuditEvent" ADD COLUMN "SecretReleaseEventId" bigint NULL
    CONSTRAINT "FK_AccessAuditEvent_SecretRelease" REFERENCES "SecretReleaseEvent"("Id");
ALTER TABLE "AccessAuditEvent" ADD COLUMN "AccessRequestId" uuid NULL
    CONSTRAINT "FK_AccessAuditEvent_AccessRequest" REFERENCES "VaultAccessRequest"("Id");
ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_OneTarget";
ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_ActionTarget";
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_OneTarget" CHECK
    (num_nonnulls("AccountRevisionId","GroupRevisionId","GroupMemberId","SpaceGrantId","ApiPermissionId","ResourceGrantId",
        "ProtectionSettingId","TrustedAddressRevisionId","SecurityRevisionId","ReauthChallengeId",
        "SecretReleaseEventId","AccessRequestId") = 1);
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
    ("ReauthChallengeId" IS NOT NULL AND "Action" = 'reauth.consumed') OR
    ("SecretReleaseEventId" IS NOT NULL AND "Action" IN ('secret.released','secret.release_denied','secret.client_report')) OR
    ("AccessRequestId" IS NOT NULL AND "Action" IN ('access_request.created','access_request.approved','access_request.rejected','access_request.cancelled')));
CREATE INDEX "IX_AccessAuditEvent_SecretRelease" ON "AccessAuditEvent"("SecretReleaseEventId") WHERE "SecretReleaseEventId" IS NOT NULL;
CREATE INDEX "IX_AccessAuditEvent_AccessRequest" ON "AccessAuditEvent"("AccessRequestId") WHERE "AccessRequestId" IS NOT NULL;

CREATE FUNCTION atlas_audit_secret_release() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO "AccessAuditEvent"("ActorId","Action","SecretReleaseEventId")
    VALUES(NEW."ActorId",
        CASE WHEN NEW."Kind" = 'client_report' THEN 'secret.client_report'
             WHEN NEW."Result" = 'released' THEN 'secret.released'
             ELSE 'secret.release_denied' END,
        NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_SecretReleaseEvent_Audit" AFTER INSERT ON "SecretReleaseEvent"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_secret_release();

-- The requester creates and cancels; a manager approves or rejects. DecidedBy is the actor of every later transition.
CREATE FUNCTION atlas_audit_access_request() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO "AccessAuditEvent"("ActorId","Action","AccessRequestId") VALUES(NEW."RequesterId",'access_request.created',NEW."Id");
    ELSIF OLD."State" = 'pending' AND NEW."State" <> 'pending' THEN
        INSERT INTO "AccessAuditEvent"("ActorId","Action","AccessRequestId") VALUES(NEW."DecidedBy",'access_request.' || NEW."State",NEW."Id");
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_VaultAccessRequest_Audit" AFTER INSERT OR UPDATE ON "VaultAccessRequest"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_access_request();

-- Same columns and order as 2026092704 (2026092702 plus the security targets) with the release and request facts appended. VaultId, SecretId, SpaceId, UserId (the
-- requester) and Role (the requested level) also come from a request; a release reports its actor only as ActorId. Note is the
-- request reason on access_request.created and the decision note on its approval or rejection.
CREATE OR REPLACE VIEW "AccessAuditView" AS
SELECT e."Id",e."ActorId",actor."DisplayName" AS "ActorName",e."OccurredAt",e."Action",
    COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId",request."RequesterId") AS "UserId",
    COALESCE(account."DisplayName",subject."DisplayName") AS "UserName",
    COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId") AS "GroupId",
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
         WHEN e."AccessRequestId" IS NOT NULL THEN request."DecisionNote" END AS "Note"
FROM "AccessAuditEvent" e
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
LEFT JOIN "UserAccount" subject ON subject."Id"=COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId",request."RequesterId")
LEFT JOIN "UserGroup" user_group ON user_group."Id"=COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId")
LEFT JOIN "Space" space ON space."Id"=COALESCE(grant_row."SpaceId",page."SpaceId",resource."VaultSpaceId",protected_vault."SpaceId",release."SpaceId",request."VaultSpaceId");
