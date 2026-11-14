-- Item access foundation (WP3 S1). Additive and behaviour-neutral: no ResourceGrant rows and no own-access page
-- exist after upgrade, and no 1.0 read or write path uses these objects yet. SpaceGrant keeps its meaning.
-- Every page is decided on its own (no inheritance along the tree): space defaults, unless OwnAccess; direct grants
-- on exactly that page; the space Admin and WikiAdmin manage every page.

-- Access mode of a page: false = space defaults, true = own access. History in PageAccessModeRevision.
ALTER TABLE "Page" ADD COLUMN "OwnAccess" boolean NOT NULL DEFAULT false;

CREATE TABLE "PageAccessModeRevision" (
    "Id" uuid PRIMARY KEY,
    "PageId" uuid NOT NULL,
    "OwnAccess" boolean NOT NULL,
    "ChangedBy" uuid NULL,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "FK_PageAccessModeRevision_Page" FOREIGN KEY ("PageId") REFERENCES "Page"("Id"),
    CONSTRAINT "FK_PageAccessModeRevision_ChangedBy" FOREIGN KEY ("ChangedBy") REFERENCES "UserAccount"("Id"),
    CONSTRAINT "CK_PageAccessModeRevision_History" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom")
);
CREATE UNIQUE INDEX "UX_PageAccessModeRevision_Current" ON "PageAccessModeRevision"("PageId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_PageAccessModeRevision_History" ON "PageAccessModeRevision"("PageId","ActiveFrom" DESC);
ALTER TABLE "PageAccessModeRevision" ADD CONSTRAINT "EX_PageAccessModeRevision_NoOverlap"
    EXCLUDE USING gist ("PageId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&);
-- The original actor and time of legacy pages are unknown; history starts at the upgrade.
INSERT INTO "PageAccessModeRevision" SELECT gen_random_uuid(),"Id","OwnAccess",NULL,now(),NULL FROM "Page";

CREATE FUNCTION atlas_version_page_access_mode() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
    IF TG_OP='UPDATE' AND NEW."OwnAccess" IS NOT DISTINCT FROM OLD."OwnAccess" THEN RETURN NEW; END IF;
    SELECT "ActiveFrom" INTO previous FROM "PageAccessModeRevision" WHERE "PageId"=NEW."Id" AND "ActiveTo" IS NULL;
    boundary := greatest(clock_timestamp(),previous+interval '1 microsecond');
    UPDATE "PageAccessModeRevision" SET "ActiveTo"=boundary WHERE "PageId"=NEW."Id" AND "ActiveTo" IS NULL;
    INSERT INTO "PageAccessModeRevision" VALUES(gen_random_uuid(),NEW."Id",NEW."OwnAccess",
        NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_Page_AccessModeHistory" AFTER INSERT OR UPDATE OF "OwnAccess" ON "Page"
    FOR EACH ROW EXECUTE FUNCTION atlas_version_page_access_mode();
CREATE TRIGGER "TR_PageAccessModeRevision_Immutable" BEFORE UPDATE OR DELETE ON "PageAccessModeRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();

-- One grant table for item targets (WP4 adds vault/secret columns and an exactly-one-target check).
-- Role uses SpaceGrant's numbering for documents: 1 DocumentReader, 2 DocumentWriter, 3 DocumentManager.
-- StartAt/ExpiresAt are business validity; ActiveFrom/ActiveTo are assignment history, as on SpaceGrant.
CREATE TABLE "ResourceGrant" (
    "Id" uuid PRIMARY KEY,
    "PageId" uuid NOT NULL,
    "UserId" uuid NULL,
    "GroupId" uuid NULL,
    "Role" integer NOT NULL,
    "StartAt" timestamptz NULL,
    "ExpiresAt" timestamptz NULL,
    "ActiveFrom" timestamptz NOT NULL DEFAULT now(),
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "FK_ResourceGrant_Page" FOREIGN KEY ("PageId") REFERENCES "Page"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ResourceGrant_User" FOREIGN KEY ("UserId") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ResourceGrant_Group" FOREIGN KEY ("GroupId") REFERENCES "UserGroup"("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_ResourceGrant_Subject" CHECK (num_nonnulls("UserId","GroupId") = 1),
    CONSTRAINT "CK_ResourceGrant_Role" CHECK ("Role" BETWEEN 1 AND 3),
    CONSTRAINT "CK_ResourceGrant_Validity" CHECK (
        ("StartAt" IS NULL OR isfinite("StartAt")) AND
        ("ExpiresAt" IS NULL OR isfinite("ExpiresAt")) AND
        ("StartAt" IS NULL OR "ExpiresAt" IS NULL OR "StartAt" < "ExpiresAt")),
    CONSTRAINT "CK_ResourceGrant_History" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom")
);
CREATE UNIQUE INDEX "UX_ResourceGrant_UserCurrent" ON "ResourceGrant"("PageId","UserId") WHERE "ActiveTo" IS NULL AND "UserId" IS NOT NULL;
CREATE UNIQUE INDEX "UX_ResourceGrant_GroupCurrent" ON "ResourceGrant"("PageId","GroupId") WHERE "ActiveTo" IS NULL AND "GroupId" IS NOT NULL;
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "EX_ResourceGrant_UserNoOverlap"
    EXCLUDE USING gist ("PageId" WITH =,"UserId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&) WHERE ("UserId" IS NOT NULL);
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "EX_ResourceGrant_GroupNoOverlap"
    EXCLUDE USING gist ("PageId" WITH =,"GroupId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&) WHERE ("GroupId" IS NOT NULL);
-- Subject lookups for lists and search ("which pages does this user or group reach").
CREATE INDEX "IX_ResourceGrant_User" ON "ResourceGrant"("UserId","PageId") WHERE "ActiveTo" IS NULL AND "UserId" IS NOT NULL;
CREATE INDEX "IX_ResourceGrant_Group" ON "ResourceGrant"("GroupId","PageId") WHERE "ActiveTo" IS NULL AND "GroupId" IS NOT NULL;
-- Latest closed interval of a subject on a page, for Temporal.NextBoundary when management re-adds a grant (as SpaceGrant in 006).
CREATE INDEX "IX_ResourceGrant_UserPreviousBoundary" ON "ResourceGrant"("PageId","UserId","ActiveTo" DESC) WHERE "UserId" IS NOT NULL;
CREATE INDEX "IX_ResourceGrant_GroupPreviousBoundary" ON "ResourceGrant"("PageId","GroupId","ActiveTo" DESC) WHERE "GroupId" IS NOT NULL;
-- Current groups of one user; UX_GroupMember_Current leads with GroupId and cannot serve "WHERE UserId = @u".
CREATE INDEX "IX_GroupMember_UserCurrent" ON "GroupMember"("UserId","GroupId") WHERE "ActiveTo" IS NULL;
CREATE TRIGGER "TR_ResourceGrant_Immutable" BEFORE UPDATE OR DELETE ON "ResourceGrant"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();

ALTER TABLE "AccessAuditEvent" ADD COLUMN "ResourceGrantId" uuid NULL REFERENCES "ResourceGrant"("Id");
ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_OneTarget";
ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_ActionTarget";
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_OneTarget" CHECK
    (num_nonnulls("AccountRevisionId","GroupRevisionId","GroupMemberId","SpaceGrantId","ApiPermissionId","ResourceGrantId") = 1);
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_ActionTarget" CHECK (
    ("AccountRevisionId" IS NOT NULL AND "Action" IN ('account.created','account.updated','credential.reset_requested','credential.password_changed','credential.activated','credential.recovered')) OR
    ("GroupRevisionId" IS NOT NULL AND "Action" IN ('group.created','group.renamed')) OR
    ("GroupMemberId" IS NOT NULL AND "Action" IN ('membership.added','membership.removed')) OR
    ("SpaceGrantId" IS NOT NULL AND "Action" IN ('grant.added','grant.removed')) OR
    ("ApiPermissionId" IS NOT NULL AND "Action" IN ('api_permission.enabled','api_permission.disabled')) OR
    ("ResourceGrantId" IS NOT NULL AND "Action" IN ('resource_grant.added','resource_grant.removed')));
CREATE INDEX "IX_AccessAuditEvent_ResourceGrant" ON "AccessAuditEvent"("ResourceGrantId") WHERE "ResourceGrantId" IS NOT NULL;

CREATE FUNCTION atlas_audit_resource_grant() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE action_name text;
BEGIN
    IF TG_OP='INSERT' AND NEW."ActiveTo" IS NULL THEN action_name:='resource_grant.added';
    ELSIF TG_OP='UPDATE' AND OLD."ActiveTo" IS NULL AND NEW."ActiveTo" IS NOT NULL THEN action_name:='resource_grant.removed';
    ELSE RETURN NEW; END IF;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","ResourceGrantId")
    VALUES(NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,action_name,NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_ResourceGrant_Audit" AFTER INSERT OR UPDATE ON "ResourceGrant"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_resource_grant();

-- Same columns and order as 022; ResourceGrantId and PageId are appended. A resource grant event reports the space its
-- page is in now, not at the time of the event: after a move between spaces, earlier events of that page show the new
-- space (PageId stays exact). WP3 S4 closes a moved page's grants, so only the history of closed grants is affected.
CREATE OR REPLACE VIEW "AccessAuditView" AS
SELECT e."Id",e."ActorId",actor."DisplayName" AS "ActorName",e."OccurredAt",e."Action",
    COALESCE(account."UserId",member."UserId",grant_row."UserId",permission."UserId",resource."UserId") AS "UserId",
    COALESCE(account."DisplayName",subject."DisplayName") AS "UserName",
    COALESCE(group_revision."GroupId",member."GroupId",grant_row."GroupId",resource."GroupId") AS "GroupId",
    COALESCE(group_revision."Name",user_group."Name") AS "GroupName",
    COALESCE(grant_row."SpaceId",page."SpaceId") AS "SpaceId",space."Name" AS "SpaceName",COALESCE(grant_row."Role",resource."Role") AS "Role",
    account."IsActive",account."IsWikiAdmin",permission."CanIssueTokens",
    e."AccountRevisionId",e."GroupRevisionId",e."GroupMemberId",e."SpaceGrantId",e."ApiPermissionId",e."ResourceGrantId",
    resource."PageId"
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
LEFT JOIN "Space" space ON space."Id"=COALESCE(grant_row."SpaceId",page."SpaceId");

-- Management includes future and expired current assignments; no copy is stored.
CREATE VIEW "ResourceGrantView" AS
SELECT g."Id", g."PageId", g."UserId", g."GroupId", COALESCE(u."DisplayName", ug."Name") AS "Name",
    g."Role", g."ActiveFrom", g."StartAt", g."ExpiresAt"
FROM "ResourceGrant" g
LEFT JOIN "UserAccount" u ON u."Id" = g."UserId"
LEFT JOIN "UserGroup" ug ON ug."Id" = g."GroupId"
WHERE g."ActiveTo" IS NULL;

-- Same statement-clock validity as EffectiveSpaceGrant: inclusive start, exclusive expiry.
CREATE VIEW "EffectiveResourceGrant" AS
SELECT "Id", "PageId", "UserId", "GroupId", "Role", "ActiveFrom", "ActiveTo", "StartAt", "ExpiresAt"
FROM "ResourceGrant"
WHERE "ActiveTo" IS NULL
    AND ("StartAt" IS NULL OR "StartAt" <= statement_timestamp())
    AND ("ExpiresAt" IS NULL OR statement_timestamp() < "ExpiresAt");

-- The highest current document role of every active user on every page, for lists and search. It must agree with
-- ResourceAccessPolicy over ResourceGrantSnapshots (checked by a PostgreSQL property test). Content state is not
-- access: trash and private drafts are filtered by the caller, as today.
CREATE VIEW "EffectivePageAccess" AS
SELECT access."UserId", access."PageId", access."SpaceId", max(access."Role") AS "Role"
FROM (
    SELECT u."Id" AS "UserId", p."Id" AS "PageId", p."SpaceId", 3 AS "Role"
    FROM "UserAccount" u CROSS JOIN "Page" p
    WHERE u."IsWikiAdmin"
    -- Each grant source has a user leg and a group leg with a plain "UserId" column (no COALESCE over an outer join),
    -- so a "WHERE UserId = @u" filter is pushed into every branch and served by a UserId-leading index.
    -- Space defaults; a space Admin (3) also manages own-access pages.
    UNION ALL
    SELECT g."UserId", p."Id", p."SpaceId", g."Role"
    FROM "EffectiveSpaceGrant" g
    JOIN "Page" p ON p."SpaceId" = g."SpaceId" AND (NOT p."OwnAccess" OR g."Role" = 3)
    WHERE g."UserId" IS NOT NULL
    UNION ALL
    SELECT m."UserId", p."Id", p."SpaceId", g."Role"
    FROM "GroupMember" m
    JOIN "EffectiveSpaceGrant" g ON g."GroupId" = m."GroupId"
    JOIN "Page" p ON p."SpaceId" = g."SpaceId" AND (NOT p."OwnAccess" OR g."Role" = 3)
    WHERE m."ActiveTo" IS NULL
    -- Direct grants apply to exactly their page, with or without own access.
    UNION ALL
    SELECT g."UserId", p."Id", p."SpaceId", g."Role"
    FROM "EffectiveResourceGrant" g
    JOIN "Page" p ON p."Id" = g."PageId"
    WHERE g."UserId" IS NOT NULL
    UNION ALL
    SELECT m."UserId", p."Id", p."SpaceId", g."Role"
    FROM "GroupMember" m
    JOIN "EffectiveResourceGrant" g ON g."GroupId" = m."GroupId"
    JOIN "Page" p ON p."Id" = g."PageId"
    WHERE m."ActiveTo" IS NULL
) access
JOIN "UserAccount" active ON active."Id" = access."UserId" AND active."IsActive"
GROUP BY access."UserId", access."PageId", access."SpaceId";

-- Forward-only: dropping item grants would silently widen or narrow access.
