-- From this migration forward only: no actor or event time is invented for legacy history.
CREATE TABLE "AccessAuditEvent" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "ActorId" uuid NULL REFERENCES "UserAccount"("Id"),
    "OccurredAt" timestamptz NOT NULL DEFAULT clock_timestamp(),
    "Action" varchar(50) NOT NULL,
    "AccountRevisionId" uuid NULL REFERENCES "UserAccountRevision"("Id"),
    "GroupRevisionId" uuid NULL REFERENCES "UserGroupRevision"("Id"),
    "GroupMemberId" uuid NULL REFERENCES "GroupMember"("Id"),
    "SpaceGrantId" uuid NULL REFERENCES "SpaceGrant"("Id"),
    "ApiPermissionId" uuid NULL REFERENCES "UserApiPermission"("Id"),
    CONSTRAINT "CK_AccessAuditEvent_OneTarget" CHECK
        (num_nonnulls("AccountRevisionId","GroupRevisionId","GroupMemberId","SpaceGrantId","ApiPermissionId") = 1),
    CONSTRAINT "CK_AccessAuditEvent_ActionTarget" CHECK (
        ("AccountRevisionId" IS NOT NULL AND "Action" IN ('account.created','account.updated','credential.reset_requested','credential.password_changed','credential.activated','credential.recovered')) OR
        ("GroupRevisionId" IS NOT NULL AND "Action" IN ('group.created','group.renamed')) OR
        ("GroupMemberId" IS NOT NULL AND "Action" IN ('membership.added','membership.removed')) OR
        ("SpaceGrantId" IS NOT NULL AND "Action" IN ('grant.added','grant.removed')) OR
        ("ApiPermissionId" IS NOT NULL AND "Action" IN ('api_permission.enabled','api_permission.disabled')))
);
CREATE INDEX "IX_AccessAuditEvent_Actor" ON "AccessAuditEvent"("ActorId","Id" DESC);
CREATE INDEX "IX_AccessAuditEvent_Account" ON "AccessAuditEvent"("AccountRevisionId") WHERE "AccountRevisionId" IS NOT NULL;
CREATE INDEX "IX_AccessAuditEvent_Grant" ON "AccessAuditEvent"("SpaceGrantId") WHERE "SpaceGrantId" IS NOT NULL;
CREATE INDEX "IX_AccessAuditEvent_Membership" ON "AccessAuditEvent"("GroupMemberId") WHERE "GroupMemberId" IS NOT NULL;
-- Read-only projection: targets and role/settings resolve from immutable history facts, not duplicated JSON.
CREATE VIEW "AccessAuditView" AS
SELECT e."Id",e."ActorId",actor."DisplayName" AS "ActorName",e."OccurredAt",e."Action",
    COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId") AS "UserId",
    COALESCE(account."DisplayName",subject."DisplayName") AS "UserName",
    COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId") AS "GroupId",
    COALESCE(group_revision."Name",user_group."Name") AS "GroupName",
    grant_row."SpaceId",space."Name" AS "SpaceName",grant_row."Role",
    account."IsActive",account."IsWikiAdmin",permission."CanIssueTokens",
    e."AccountRevisionId",e."GroupRevisionId",e."GroupMemberId",e."SpaceGrantId",e."ApiPermissionId"
FROM "AccessAuditEvent" e
LEFT JOIN "UserAccount" actor ON actor."Id"=e."ActorId"
LEFT JOIN "UserAccountRevision" account ON account."Id"=e."AccountRevisionId"
LEFT JOIN "UserGroupRevision" group_revision ON group_revision."Id"=e."GroupRevisionId"
LEFT JOIN "GroupMember" member ON member."Id"=e."GroupMemberId"
LEFT JOIN "SpaceGrant" grant_row ON grant_row."Id"=e."SpaceGrantId"
LEFT JOIN "UserApiPermission" permission ON permission."Id"=e."ApiPermissionId"
LEFT JOIN "UserAccount" subject ON subject."Id"=COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId")
LEFT JOIN "UserGroup" user_group ON user_group."Id"=COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId")
LEFT JOIN "Space" space ON space."Id"=grant_row."SpaceId";

CREATE FUNCTION atlas_access_audit_history_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Access history cannot be deleted' USING ERRCODE='23514'; END IF;
    IF (to_jsonb(NEW)-'ActiveTo') IS DISTINCT FROM (to_jsonb(OLD)-'ActiveTo')
        OR (OLD."ActiveTo" IS NOT NULL AND NEW."ActiveTo" IS DISTINCT FROM OLD."ActiveTo") THEN
        RAISE EXCEPTION 'Access history is immutable; close the current interval and insert a new row' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_UserAccountRevision_Immutable" BEFORE UPDATE OR DELETE ON "UserAccountRevision" FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();
CREATE TRIGGER "TR_UserGroupRevision_Immutable" BEFORE UPDATE OR DELETE ON "UserGroupRevision" FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();
CREATE TRIGGER "TR_GroupMember_Immutable" BEFORE UPDATE OR DELETE ON "GroupMember" FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();
CREATE TRIGGER "TR_SpaceGrant_Immutable" BEFORE UPDATE OR DELETE ON "SpaceGrant" FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();
CREATE TRIGGER "TR_UserApiPermission_Immutable" BEFORE UPDATE OR DELETE ON "UserApiPermission" FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();
CREATE FUNCTION atlas_access_audit_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'Access audit is append-only' USING ERRCODE='23514'; END $$;
CREATE TRIGGER "TR_AccessAuditEvent_Immutable" BEFORE UPDATE OR DELETE ON "AccessAuditEvent" FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_immutable();

CREATE FUNCTION atlas_audit_account_revision() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE previous "UserAccountRevision"%ROWTYPE;
BEGIN
    SELECT * INTO previous FROM "UserAccountRevision" WHERE "UserId"=NEW."UserId" AND "Id"<>NEW."Id" ORDER BY "ActiveFrom" DESC LIMIT 1;
    -- Password rehashes/session stamp rotations are not account metadata edits.
    IF previous."Id" IS NOT NULL AND
        ROW(NEW."Username",NEW."DisplayName",NEW."IsActive",NEW."IsWikiAdmin") IS NOT DISTINCT FROM
        ROW(previous."Username",previous."DisplayName",previous."IsActive",previous."IsWikiAdmin") THEN RETURN NEW; END IF;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","AccountRevisionId")
    VALUES(NEW."ChangedBy",CASE WHEN previous."Id" IS NULL THEN 'account.created' ELSE 'account.updated' END,NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_UserAccountRevision_Audit" AFTER INSERT ON "UserAccountRevision" FOR EACH ROW EXECUTE FUNCTION atlas_audit_account_revision();
CREATE FUNCTION atlas_audit_group_revision() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE previous "UserGroupRevision"%ROWTYPE;
BEGIN
    SELECT * INTO previous FROM "UserGroupRevision" WHERE "GroupId"=NEW."GroupId" AND "Id"<>NEW."Id" ORDER BY "ActiveFrom" DESC LIMIT 1;
    IF previous."Id" IS NOT NULL AND previous."Name"=NEW."Name" THEN RETURN NEW; END IF;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","GroupRevisionId")
    VALUES(NEW."ChangedBy",CASE WHEN previous."Id" IS NULL THEN 'group.created' ELSE 'group.renamed' END,NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_UserGroupRevision_Audit" AFTER INSERT ON "UserGroupRevision" FOR EACH ROW EXECUTE FUNCTION atlas_audit_group_revision();
CREATE FUNCTION atlas_audit_group_member() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE action_name text;
BEGIN
    IF TG_OP='INSERT' AND NEW."ActiveTo" IS NULL THEN action_name:='membership.added';
    ELSIF TG_OP='UPDATE' AND OLD."ActiveTo" IS NULL AND NEW."ActiveTo" IS NOT NULL THEN action_name:='membership.removed';
    ELSE RETURN NEW; END IF;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","GroupMemberId")
    VALUES(NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,action_name,NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_GroupMember_Audit" AFTER INSERT OR UPDATE ON "GroupMember" FOR EACH ROW EXECUTE FUNCTION atlas_audit_group_member();
CREATE FUNCTION atlas_audit_space_grant() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE action_name text;
BEGIN
    IF TG_OP='INSERT' AND NEW."ActiveTo" IS NULL THEN action_name:='grant.added';
    ELSIF TG_OP='UPDATE' AND OLD."ActiveTo" IS NULL AND NEW."ActiveTo" IS NOT NULL THEN action_name:='grant.removed';
    ELSE RETURN NEW; END IF;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","SpaceGrantId")
    VALUES(NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,action_name,NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_SpaceGrant_Audit" AFTER INSERT OR UPDATE ON "SpaceGrant" FOR EACH ROW EXECUTE FUNCTION atlas_audit_space_grant();
CREATE FUNCTION atlas_audit_api_permission() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW."ActiveTo" IS NOT NULL THEN RETURN NEW; END IF;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","ApiPermissionId")
    VALUES(NEW."ChangedById",CASE WHEN NEW."CanIssueTokens" THEN 'api_permission.enabled' ELSE 'api_permission.disabled' END,NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_UserApiPermission_Audit" AFTER INSERT ON "UserApiPermission" FOR EACH ROW EXECUTE FUNCTION atlas_audit_api_permission();

-- Explicit semantic events: no passwords, hashes, bearer secrets, reset tokens or session stamps enter the audit.
CREATE FUNCTION atlas_record_credential_audit(subject_id uuid, action_name text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE revision_id uuid;
BEGIN
    IF action_name NOT IN ('credential.reset_requested','credential.password_changed','credential.activated','credential.recovered') THEN
        RAISE EXCEPTION 'Invalid credential audit action' USING ERRCODE='23514';
    END IF;
    SELECT "Id" INTO STRICT revision_id FROM "UserAccountRevision" WHERE "UserId"=subject_id AND "ActiveTo" IS NULL;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","AccountRevisionId")
    VALUES(NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,action_name,revision_id);
END $$;
