-- Protection modes, trusted networks and proxies, and fresh re-authentication (WP4 V4). Additive. A fresh configuration is the
-- owner default: instance mode "always" and no trusted network. The server imports the explicit Http:TrustedProxies addresses
-- once, at its first start on this schema (SecurityConfiguration.ProxiesImportedAt), as exact /32 or /128 proxies with one hop,
-- so trust is never widened; afterwards the database is the only source of truth.

-- One row: the version of the security configuration. Every security change (a protection mode on any level, a trusted network,
-- a trusted proxy, the hop limit) increments Version in its own transaction under FOR UPDATE on this row, so a change activates
-- atomically, and an evaluation holding FOR SHARE on it reads one consistent version. ProxyHopLimit is how many trusted proxies
-- a request may pass before its client address counts as unclear.
CREATE TABLE "SecurityConfiguration" (
    "Id" smallint PRIMARY KEY,
    "Version" bigint NOT NULL,
    "ProxyHopLimit" smallint NOT NULL,
    "ProxiesImportedAt" timestamptz NULL,
    CONSTRAINT "CK_SecurityConfiguration_Single" CHECK ("Id" = 1),
    CONSTRAINT "CK_SecurityConfiguration_Version" CHECK ("Version" >= 1),
    CONSTRAINT "CK_SecurityConfiguration_Hops" CHECK ("ProxyHopLimit" BETWEEN 1 AND 5)
);

-- Trigger-written history of every configuration version: who activated it and the hop limit it carried.
CREATE TABLE "SecurityConfigurationRevision" (
    "Id" uuid PRIMARY KEY,
    "Version" bigint NOT NULL,
    "ProxyHopLimit" smallint NOT NULL,
    "ChangedBy" uuid NULL,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "FK_SecurityConfigurationRevision_ChangedBy" FOREIGN KEY ("ChangedBy") REFERENCES "UserAccount"("Id"),
    CONSTRAINT "UX_SecurityConfigurationRevision_Version" UNIQUE ("Version"),
    CONSTRAINT "CK_SecurityConfigurationRevision_History" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom")
);
CREATE UNIQUE INDEX "UX_SecurityConfigurationRevision_Current" ON "SecurityConfigurationRevision"((1)) WHERE "ActiveTo" IS NULL;
ALTER TABLE "SecurityConfigurationRevision" ADD CONSTRAINT "EX_SecurityConfigurationRevision_NoOverlap"
    EXCLUDE USING gist (tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&);

CREATE FUNCTION atlas_version_security_configuration() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
    IF TG_OP = 'UPDATE' AND NEW."Version" = OLD."Version" THEN
        IF NEW."ProxyHopLimit" <> OLD."ProxyHopLimit" THEN
            RAISE EXCEPTION 'A security configuration change must increment Version' USING ERRCODE='23514';
        END IF;
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' AND NEW."Version" <> OLD."Version" + 1 THEN
        RAISE EXCEPTION 'Security configuration versions increase by one' USING ERRCODE='23514';
    END IF;
    SELECT "ActiveFrom" INTO previous FROM "SecurityConfigurationRevision" WHERE "ActiveTo" IS NULL;
    boundary := greatest(clock_timestamp(),previous+interval '1 microsecond');
    UPDATE "SecurityConfigurationRevision" SET "ActiveTo"=boundary WHERE "ActiveTo" IS NULL;
    INSERT INTO "SecurityConfigurationRevision" VALUES(gen_random_uuid(),NEW."Version",NEW."ProxyHopLimit",
        NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_SecurityConfiguration_History" AFTER INSERT OR UPDATE ON "SecurityConfiguration"
    FOR EACH ROW EXECUTE FUNCTION atlas_version_security_configuration();
CREATE TRIGGER "TR_SecurityConfigurationRevision_Immutable" BEFORE UPDATE OR DELETE ON "SecurityConfigurationRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();
CREATE FUNCTION atlas_security_configuration_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'The security configuration row cannot be deleted' USING ERRCODE='23514'; END $$;
CREATE TRIGGER "TR_SecurityConfiguration_NoDelete" BEFORE DELETE ON "SecurityConfiguration"
    FOR EACH ROW EXECUTE FUNCTION atlas_security_configuration_guard();

INSERT INTO "SecurityConfiguration"("Id","Version","ProxyHopLimit","ProxiesImportedAt") VALUES(1,1,1,NULL);

-- A named trusted address rule. Purpose network: a LAN, VPN or other range whose verified clients skip fresh re-authentication in
-- the "outside trusted networks" mode (never in "always", never the access check or the audit). Purpose proxy: a reverse proxy
-- whose X-Forwarded-For and X-Forwarded-Proto Atlas accepts, up to ProxyHopLimit hops. Range is a normalized CIDR (host bits
-- clear; a single address is /32 or /128). Nothing is predefined. Removing a rule archives it (ArchivedAt); history in
-- TrustedAddressRevision.
CREATE TABLE "TrustedAddress" (
    "Id" uuid PRIMARY KEY,
    "Purpose" varchar(8) NOT NULL,
    "Name" varchar(200) NOT NULL,
    "Kind" varchar(8) NULL,
    "Range" varchar(50) NOT NULL,
    "IsActive" boolean NOT NULL,
    "ArchivedAt" timestamptz NULL,
    CONSTRAINT "CK_TrustedAddress_Purpose" CHECK ("Purpose" IN ('network','proxy')),
    CONSTRAINT "CK_TrustedAddress_Name" CHECK (length(btrim("Name")) > 0),
    CONSTRAINT "CK_TrustedAddress_Kind" CHECK (("Purpose" = 'network' AND "Kind" IN ('lan','vpn','other')) OR ("Purpose" = 'proxy' AND "Kind" IS NULL)),
    -- The cast fails for anything PostgreSQL does not accept as a network, including host bits beyond the prefix.
    CONSTRAINT "CK_TrustedAddress_Range" CHECK (CAST("Range" AS cidr) IS NOT NULL)
);
CREATE UNIQUE INDEX "UX_TrustedAddress_Name" ON "TrustedAddress"("Purpose",lower("Name")) WHERE "ArchivedAt" IS NULL;

CREATE TABLE "TrustedAddressRevision" (
    "Id" uuid PRIMARY KEY,
    "AddressId" uuid NOT NULL,
    "Purpose" varchar(8) NOT NULL,
    "Name" varchar(200) NOT NULL,
    "Kind" varchar(8) NULL,
    "Range" varchar(50) NOT NULL,
    "IsActive" boolean NOT NULL,
    "ArchivedAt" timestamptz NULL,
    "ConfigurationVersion" bigint NOT NULL,
    "ChangedBy" uuid NULL,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "FK_TrustedAddressRevision_Address" FOREIGN KEY ("AddressId") REFERENCES "TrustedAddress"("Id"),
    CONSTRAINT "FK_TrustedAddressRevision_ChangedBy" FOREIGN KEY ("ChangedBy") REFERENCES "UserAccount"("Id"),
    CONSTRAINT "CK_TrustedAddressRevision_History" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom")
);
CREATE UNIQUE INDEX "UX_TrustedAddressRevision_Current" ON "TrustedAddressRevision"("AddressId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_TrustedAddressRevision_History" ON "TrustedAddressRevision"("AddressId","ActiveFrom" DESC);
ALTER TABLE "TrustedAddressRevision" ADD CONSTRAINT "EX_TrustedAddressRevision_NoOverlap"
    EXCLUDE USING gist ("AddressId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&);

-- The revision records the configuration version the change activated; a rule is changed only together with a version increment.
CREATE FUNCTION atlas_version_trusted_address() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW."Purpose",NEW."Name",NEW."Kind",NEW."Range",NEW."IsActive",NEW."ArchivedAt")
        IS NOT DISTINCT FROM (OLD."Purpose",OLD."Name",OLD."Kind",OLD."Range",OLD."IsActive",OLD."ArchivedAt") THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' AND (NEW."Purpose" <> OLD."Purpose" OR OLD."ArchivedAt" IS NOT NULL) THEN
        RAISE EXCEPTION 'A trusted address keeps its purpose and an archived one stays archived' USING ERRCODE='23514';
    END IF;
    SELECT "ActiveFrom" INTO previous FROM "TrustedAddressRevision" WHERE "AddressId"=NEW."Id" AND "ActiveTo" IS NULL;
    boundary := greatest(clock_timestamp(),previous+interval '1 microsecond');
    UPDATE "TrustedAddressRevision" SET "ActiveTo"=boundary WHERE "AddressId"=NEW."Id" AND "ActiveTo" IS NULL;
    INSERT INTO "TrustedAddressRevision" VALUES(gen_random_uuid(),NEW."Id",NEW."Purpose",NEW."Name",NEW."Kind",NEW."Range",
        NEW."IsActive",NEW."ArchivedAt",(SELECT "Version" FROM "SecurityConfiguration" WHERE "Id"=1),
        NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_TrustedAddress_History" AFTER INSERT OR UPDATE ON "TrustedAddress"
    FOR EACH ROW EXECUTE FUNCTION atlas_version_trusted_address();
CREATE TRIGGER "TR_TrustedAddressRevision_Immutable" BEFORE UPDATE OR DELETE ON "TrustedAddressRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();
CREATE FUNCTION atlas_trusted_address_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'Trusted addresses are archived, not deleted' USING ERRCODE='23514'; END $$;
CREATE TRIGGER "TR_TrustedAddress_NoDelete" BEFORE DELETE ON "TrustedAddress"
    FOR EACH ROW EXECUTE FUNCTION atlas_trusted_address_guard();

-- The explicit protection mode of one level: the instance (VaultId and SecretId NULL), a vault, or a secret of that vault. A level
-- without a current row inherits from the next one up (secret, vault, instance); the instance always has a current row. Rows are
-- immutable except for closing (ActiveTo), like ResourceGrant; changing a mode closes the current row and inserts the next one at
-- the following boundary, inheriting only closes it. ConfigurationVersion is the version the change activated.
CREATE TABLE "ProtectionSetting" (
    "Id" uuid PRIMARY KEY,
    "VaultId" uuid NULL,
    "SecretId" uuid NULL,
    "Mode" varchar(32) NOT NULL,
    "ConfigurationVersion" bigint NOT NULL,
    "ChangedBy" uuid NULL,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "FK_ProtectionSetting_Vault" FOREIGN KEY ("VaultId") REFERENCES "Vault"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ProtectionSetting_Secret" FOREIGN KEY ("SecretId","VaultId") REFERENCES "VaultSecret"("Id","VaultId") ON DELETE RESTRICT,
    CONSTRAINT "FK_ProtectionSetting_ChangedBy" FOREIGN KEY ("ChangedBy") REFERENCES "UserAccount"("Id"),
    CONSTRAINT "CK_ProtectionSetting_Target" CHECK ("SecretId" IS NULL OR "VaultId" IS NOT NULL),
    CONSTRAINT "CK_ProtectionSetting_Mode" CHECK ("Mode" IN ('never','outsideTrustedNetworks','always')),
    CONSTRAINT "CK_ProtectionSetting_History" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom")
);
CREATE UNIQUE INDEX "UX_ProtectionSetting_Current" ON "ProtectionSetting"
    (COALESCE("VaultId",'00000000-0000-0000-0000-000000000000'::uuid),COALESCE("SecretId",'00000000-0000-0000-0000-000000000000'::uuid))
    WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_ProtectionSetting_History" ON "ProtectionSetting"("VaultId","SecretId","ActiveTo" DESC);
ALTER TABLE "ProtectionSetting" ADD CONSTRAINT "EX_ProtectionSetting_NoOverlap" EXCLUDE USING gist (
    (COALESCE("VaultId",'00000000-0000-0000-0000-000000000000'::uuid)) WITH =,
    (COALESCE("SecretId",'00000000-0000-0000-0000-000000000000'::uuid)) WITH =,
    tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&);
CREATE TRIGGER "TR_ProtectionSetting_Immutable" BEFORE UPDATE OR DELETE ON "ProtectionSetting"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();

INSERT INTO "ProtectionSetting"("Id","VaultId","SecretId","Mode","ConfigurationVersion","ChangedBy","ActiveFrom","ActiveTo")
VALUES(gen_random_uuid(),NULL,NULL,'always',1,NULL,clock_timestamp(),NULL);

-- One fresh re-authentication. Binds the account, a SHA-256 of its security stamp, the Atlas session (atlas_session claim), the
-- operation (security: a security change; reveal and copy: a value release of V5; selftest: the provider check, which releases
-- nothing)
-- and its target (instance: global security, vault, secret). IssuedAt and ExpiresAt (+5 minutes to complete it) come from the
-- database clock. VerifiedAt is set by the password check or the provider callback; ConsumedAt once, atomically, by the change or
-- release it proves, within two minutes of VerifiedAt. Failure records why a verification was refused (no secret, no token).
-- AuthTime is the provider's auth_time claim, when one was seen. Operational state, not history.
CREATE TABLE "ReauthChallenge" (
    "Id" uuid PRIMARY KEY,
    "UserId" uuid NOT NULL,
    "SecurityStampHash" bytea NOT NULL,
    "SessionId" varchar(64) NOT NULL,
    "Operation" varchar(16) NOT NULL,
    "TargetKind" varchar(16) NOT NULL,
    "TargetId" uuid NULL,
    "Method" varchar(16) NOT NULL,
    "ProviderId" varchar(100) NULL,
    "IssuedAt" timestamptz NOT NULL,
    "ExpiresAt" timestamptz NOT NULL,
    "VerifiedAt" timestamptz NULL,
    "ConsumedAt" timestamptz NULL,
    "FailedAt" timestamptz NULL,
    "Failure" varchar(32) NULL,
    "AuthTime" timestamptz NULL,
    CONSTRAINT "FK_ReauthChallenge_User" FOREIGN KEY ("UserId") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_ReauthChallenge_Stamp" CHECK (octet_length("SecurityStampHash") = 32),
    CONSTRAINT "CK_ReauthChallenge_Operation" CHECK ("Operation" IN ('security','reveal','copy','selftest')),
    CONSTRAINT "CK_ReauthChallenge_Target" CHECK (
        ("TargetKind" IN ('vault','secret') AND "TargetId" IS NOT NULL) OR ("TargetKind" IN ('instance','none') AND "TargetId" IS NULL)),
    CONSTRAINT "CK_ReauthChallenge_OperationTarget" CHECK (
        ("Operation" = 'security' AND "TargetKind" IN ('instance','vault','secret')) OR
        ("Operation" IN ('reveal','copy') AND "TargetKind" = 'secret') OR
        ("Operation" = 'selftest' AND "TargetKind" = 'none' AND "Method" = 'oidc')),
    CONSTRAINT "CK_ReauthChallenge_Method" CHECK (("Method" = 'password' AND "ProviderId" IS NULL) OR ("Method" = 'oidc' AND "ProviderId" IS NOT NULL)),
    CONSTRAINT "CK_ReauthChallenge_Expiry" CHECK ("ExpiresAt" > "IssuedAt"),
    CONSTRAINT "CK_ReauthChallenge_Verified" CHECK ("VerifiedAt" IS NULL OR ("VerifiedAt" >= "IssuedAt" AND "VerifiedAt" <= "ExpiresAt")),
    CONSTRAINT "CK_ReauthChallenge_Consumed" CHECK ("ConsumedAt" IS NULL OR ("VerifiedAt" IS NOT NULL AND "ConsumedAt" >= "VerifiedAt")),
    CONSTRAINT "CK_ReauthChallenge_Failure" CHECK (("FailedAt" IS NULL) = ("Failure" IS NULL))
);
CREATE INDEX "IX_ReauthChallenge_User" ON "ReauthChallenge"("UserId","IssuedAt" DESC);

-- The latest fresh-authentication evidence of one OIDC provider registration: from the WikiAdmin self-test or from a real
-- re-authentication. Supported only after a validated id_token carried an auth_time no older than the challenge; unsupported
-- when auth_time was missing or stale. No row (or another issuer than configured) means unknown. Derived booleans and times only;
-- the id_token is never stored.
CREATE TABLE "OidcFreshAuthStatus" (
    "ProviderId" varchar(100) PRIMARY KEY,
    "Issuer" varchar(1000) NOT NULL,
    "Verdict" varchar(16) NOT NULL,
    "AuthTimePresent" boolean NOT NULL,
    "AuthTimeFresh" boolean NOT NULL,
    "SelfTest" boolean NOT NULL,
    "CheckedAt" timestamptz NOT NULL,
    "CheckedBy" uuid NULL,
    CONSTRAINT "FK_OidcFreshAuthStatus_CheckedBy" FOREIGN KEY ("CheckedBy") REFERENCES "UserAccount"("Id"),
    CONSTRAINT "CK_OidcFreshAuthStatus_Verdict" CHECK (
        ("Verdict" = 'supported' AND "AuthTimePresent" AND "AuthTimeFresh") OR ("Verdict" = 'unsupported' AND NOT ("AuthTimePresent" AND "AuthTimeFresh")))
);

-- Audit: protection changes (protection.set when a level gets an explicit mode, protection.cleared when that row closes, so a
-- change is cleared + set like a grant change), trusted address changes (created, updated, removed), hop limit changes and the
-- consumption of a re-authentication proof by the change or release it authorized (reauth.consumed, same transaction).
ALTER TABLE "AccessAuditEvent" ADD COLUMN "ProtectionSettingId" uuid NULL REFERENCES "ProtectionSetting"("Id");
ALTER TABLE "AccessAuditEvent" ADD COLUMN "TrustedAddressRevisionId" uuid NULL REFERENCES "TrustedAddressRevision"("Id");
ALTER TABLE "AccessAuditEvent" ADD COLUMN "SecurityRevisionId" uuid NULL REFERENCES "SecurityConfigurationRevision"("Id");
ALTER TABLE "AccessAuditEvent" ADD COLUMN "ReauthChallengeId" uuid NULL REFERENCES "ReauthChallenge"("Id");
ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_OneTarget";
ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_ActionTarget";
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_OneTarget" CHECK
    (num_nonnulls("AccountRevisionId","GroupRevisionId","GroupMemberId","SpaceGrantId","ApiPermissionId","ResourceGrantId",
        "ProtectionSettingId","TrustedAddressRevisionId","SecurityRevisionId","ReauthChallengeId") = 1);
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
    ("ReauthChallengeId" IS NOT NULL AND "Action" = 'reauth.consumed'));
CREATE INDEX "IX_AccessAuditEvent_Protection" ON "AccessAuditEvent"("ProtectionSettingId") WHERE "ProtectionSettingId" IS NOT NULL;
CREATE INDEX "IX_AccessAuditEvent_TrustedAddress" ON "AccessAuditEvent"("TrustedAddressRevisionId") WHERE "TrustedAddressRevisionId" IS NOT NULL;

CREATE FUNCTION atlas_audit_protection_setting() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE action_name text;
BEGIN
    IF TG_OP='INSERT' AND NEW."ActiveTo" IS NULL THEN action_name:='protection.set';
    ELSIF TG_OP='UPDATE' AND OLD."ActiveTo" IS NULL AND NEW."ActiveTo" IS NOT NULL THEN action_name:='protection.cleared';
    ELSE RETURN NEW; END IF;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","ProtectionSettingId")
    VALUES(NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,action_name,NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_ProtectionSetting_Audit" AFTER INSERT OR UPDATE ON "ProtectionSetting"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_protection_setting();

CREATE FUNCTION atlas_audit_trusted_address_revision() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE previous_count int; action_name text;
BEGIN
    SELECT count(*) INTO previous_count FROM "TrustedAddressRevision" WHERE "AddressId"=NEW."AddressId" AND "Id"<>NEW."Id";
    action_name := CASE WHEN NEW."Purpose"='network' THEN 'trusted_network.' ELSE 'trusted_proxy.' END
        || CASE WHEN previous_count = 0 THEN 'created' WHEN NEW."ArchivedAt" IS NOT NULL THEN 'removed' ELSE 'updated' END;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","TrustedAddressRevisionId") VALUES(NEW."ChangedBy",action_name,NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_TrustedAddressRevision_Audit" AFTER INSERT ON "TrustedAddressRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_trusted_address_revision();

CREATE FUNCTION atlas_audit_security_configuration_revision() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE previous_hops smallint;
BEGIN
    SELECT "ProxyHopLimit" INTO previous_hops FROM "SecurityConfigurationRevision" WHERE "Version"=NEW."Version"-1;
    IF previous_hops IS NOT NULL AND previous_hops <> NEW."ProxyHopLimit" THEN
        INSERT INTO "AccessAuditEvent"("ActorId","Action","SecurityRevisionId") VALUES(NEW."ChangedBy",'security.proxy_hops_changed',NEW."Id");
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_SecurityConfigurationRevision_Audit" AFTER INSERT ON "SecurityConfigurationRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_security_configuration_revision();

-- Explicit semantic event, as atlas_record_credential_audit: the proof and the change it authorized share the transaction.
CREATE FUNCTION atlas_record_reauth_audit(challenge_id uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM "ReauthChallenge" WHERE "Id"=challenge_id AND "ConsumedAt" IS NOT NULL) THEN
        RAISE EXCEPTION 'Only a consumed re-authentication is audited' USING ERRCODE='23514';
    END IF;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","ReauthChallengeId")
    VALUES(NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,'reauth.consumed',challenge_id);
END $$;

-- Same columns and order as 2026092702; the security targets are appended. A protection event of a vault or secret reports the
-- vault's space, VaultId and SecretId like a grant event.
CREATE OR REPLACE VIEW "AccessAuditView" AS
SELECT e."Id",e."ActorId",actor."DisplayName" AS "ActorName",e."OccurredAt",e."Action",
    COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId") AS "UserId",
    COALESCE(account."DisplayName",subject."DisplayName") AS "UserName",
    COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId") AS "GroupId",
    COALESCE(group_revision."Name",user_group."Name") AS "GroupName",
    COALESCE(grant_row."SpaceId",page."SpaceId",resource."VaultSpaceId",protected_vault."SpaceId") AS "SpaceId",space."Name" AS "SpaceName",
    COALESCE(grant_row."Role",resource."Role") AS "Role",
    account."IsActive",account."IsWikiAdmin",permission."CanIssueTokens",
    e."AccountRevisionId",e."GroupRevisionId",e."GroupMemberId",e."SpaceGrantId",e."ApiPermissionId",e."ResourceGrantId",
    resource."PageId",COALESCE(resource."VaultId",protection."VaultId") AS "VaultId",COALESCE(resource."SecretId",protection."SecretId") AS "SecretId",
    e."ProtectionSettingId",protection."Mode" AS "ProtectionMode",e."TrustedAddressRevisionId",address."Name" AS "TrustedAddressName",
    address."Range" AS "TrustedAddressRange",e."SecurityRevisionId",e."ReauthChallengeId",
    COALESCE(protection."ConfigurationVersion",address."ConfigurationVersion",security_revision."Version") AS "ConfigurationVersion"
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
LEFT JOIN "UserAccount" subject ON subject."Id"=COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId")
LEFT JOIN "UserGroup" user_group ON user_group."Id"=COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId")
LEFT JOIN "Space" space ON space."Id"=COALESCE(grant_row."SpaceId",page."SpaceId",resource."VaultSpaceId",protected_vault."SpaceId");

-- Forward-only: dropping protection settings or trusted addresses would silently weaken protection and lose its history.
