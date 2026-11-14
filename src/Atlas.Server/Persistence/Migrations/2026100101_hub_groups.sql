-- Expand only: no configured connection, external binding or derived right is created by migration.
CREATE TABLE "HubConnection" ("Id" uuid PRIMARY KEY);
CREATE TABLE "HubConnectionRevision" (
    "Id" uuid PRIMARY KEY,
    "ConnectionId" uuid NOT NULL REFERENCES "HubConnection"("Id"),
    "Issuer" text NOT NULL CHECK (length(btrim("Issuer")) > 0),
    "BaseUrl" text NOT NULL CHECK (length(btrim("BaseUrl")) > 0),
    "ProtectedToken" text NOT NULL CHECK (length("ProtectedToken") > 0),
    "Mode" smallint NOT NULL DEFAULT 0 CHECK ("Mode" IN (0,1,2)),
    "ChangedById" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "CK_HubConnectionRevision_Validity" CHECK (
        isfinite("ActiveFrom") AND ("ActiveTo" IS NULL OR (isfinite("ActiveTo") AND "ActiveTo" > "ActiveFrom")))
);
CREATE UNIQUE INDEX "UX_HubConnectionRevision_Current" ON "HubConnectionRevision"("ConnectionId") WHERE "ActiveTo" IS NULL;
CREATE UNIQUE INDEX "UX_HubConnectionRevision_Singleton" ON "HubConnectionRevision"((true)) WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_HubConnectionRevision_History" ON "HubConnectionRevision"("ConnectionId","ActiveFrom" DESC);
CREATE INDEX "IX_HubConnectionRevision_ChangedBy" ON "HubConnectionRevision"("ChangedById");
ALTER TABLE "HubConnectionRevision" ADD CONSTRAINT "EX_HubConnectionRevision_NoOverlap"
    EXCLUDE USING gist ("ConnectionId" WITH =, tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&);
CREATE TRIGGER "TR_HubConnectionRevision_Immutable" BEFORE UPDATE OR DELETE ON "HubConnectionRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();
CREATE FUNCTION atlas_hub_connection_issuer_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    -- Serialize revision creation on the stable identity, including the first revision.
    PERFORM 1 FROM "HubConnection" WHERE "Id"=NEW."ConnectionId" FOR UPDATE;
    IF EXISTS (SELECT 1 FROM "HubConnectionRevision" WHERE "ConnectionId"=NEW."ConnectionId" AND "Issuer"<>NEW."Issuer") THEN
        RAISE EXCEPTION 'A Hub connection cannot change issuer' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_HubConnectionRevision_Issuer" BEFORE INSERT ON "HubConnectionRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_hub_connection_issuer_guard();

CREATE TABLE "HubGroupBinding" (
    "GroupId" uuid PRIMARY KEY REFERENCES "UserGroup"("Id"),
    "ConnectionId" uuid NOT NULL REFERENCES "HubConnection"("Id"),
    "ExternalGroupId" text NOT NULL CHECK (length(btrim("ExternalGroupId")) > 0),
    "Enabled" boolean NOT NULL DEFAULT true,
    "GrantsWikiAdmin" boolean NOT NULL DEFAULT false,
    CONSTRAINT "UX_HubGroupBinding_External" UNIQUE ("ConnectionId","ExternalGroupId")
);
CREATE TABLE "HubGroupBindingRevision" (
    "Id" uuid PRIMARY KEY,
    "GroupId" uuid NOT NULL REFERENCES "HubGroupBinding"("GroupId"),
    "ConnectionId" uuid NOT NULL REFERENCES "HubConnection"("Id"),
    "ExternalGroupId" text NOT NULL,
    "Enabled" boolean NOT NULL,
    "GrantsWikiAdmin" boolean NOT NULL,
    "ChangedBy" uuid NULL REFERENCES "UserAccount"("Id"),
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "CK_HubGroupBindingRevision_Validity" CHECK (
        isfinite("ActiveFrom") AND ("ActiveTo" IS NULL OR (isfinite("ActiveTo") AND "ActiveTo" > "ActiveFrom")))
);
CREATE UNIQUE INDEX "UX_HubGroupBindingRevision_Current" ON "HubGroupBindingRevision"("GroupId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_HubGroupBindingRevision_History" ON "HubGroupBindingRevision"("GroupId","ActiveFrom" DESC);
CREATE INDEX "IX_HubGroupBindingRevision_Connection" ON "HubGroupBindingRevision"("ConnectionId");
CREATE INDEX "IX_HubGroupBindingRevision_ChangedBy" ON "HubGroupBindingRevision"("ChangedBy");
ALTER TABLE "HubGroupBindingRevision" ADD CONSTRAINT "EX_HubGroupBindingRevision_NoOverlap"
    EXCLUDE USING gist ("GroupId" WITH =, tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&);
CREATE TRIGGER "TR_HubGroupBindingRevision_Immutable" BEFORE UPDATE OR DELETE ON "HubGroupBindingRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();
CREATE FUNCTION atlas_hub_binding_identity_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP='DELETE' THEN
        RAISE EXCEPTION 'Disable a Hub binding; deleting it would turn external membership into local membership' USING ERRCODE='23514';
    END IF;
    IF NEW."GroupId"<>OLD."GroupId" OR NEW."ConnectionId"<>OLD."ConnectionId" OR NEW."ExternalGroupId"<>OLD."ExternalGroupId" THEN
        RAISE EXCEPTION 'A Hub binding cannot change identity' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_HubGroupBinding_Identity" BEFORE UPDATE OR DELETE ON "HubGroupBinding"
    FOR EACH ROW EXECUTE FUNCTION atlas_hub_binding_identity_guard();
CREATE FUNCTION atlas_version_hub_group_binding() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
    IF TG_OP='UPDATE' AND NEW IS NOT DISTINCT FROM OLD THEN RETURN NEW; END IF;
    SELECT "ActiveFrom" INTO previous FROM "HubGroupBindingRevision" WHERE "GroupId"=NEW."GroupId" AND "ActiveTo" IS NULL;
    boundary := greatest(clock_timestamp(), previous + interval '1 microsecond');
    UPDATE "HubGroupBindingRevision" SET "ActiveTo"=boundary WHERE "GroupId"=NEW."GroupId" AND "ActiveTo" IS NULL;
    INSERT INTO "HubGroupBindingRevision"
        ("Id","GroupId","ConnectionId","ExternalGroupId","Enabled","GrantsWikiAdmin","ChangedBy","ActiveFrom","ActiveTo")
    VALUES (gen_random_uuid(),NEW."GroupId",NEW."ConnectionId",NEW."ExternalGroupId",NEW."Enabled",NEW."GrantsWikiAdmin",
        NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_HubGroupBinding_History" AFTER INSERT OR UPDATE ON "HubGroupBinding"
    FOR EACH ROW EXECUTE FUNCTION atlas_version_hub_group_binding();

-- UserId is a constrained projection of ExternalIdentity.UserId, not an independently editable link.
-- The composite FK prevents drift; the connection/user unique key enforces one Hub identity per local account.
ALTER TABLE "ExternalIdentity" ADD CONSTRAINT "UX_ExternalIdentity_IdUser" UNIQUE ("Id","UserId");
CREATE TABLE "HubIdentityState" (
    "ExternalIdentityId" uuid PRIMARY KEY,
    "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "ConnectionId" uuid NOT NULL REFERENCES "HubConnection"("Id"),
    "ExternalUserId" text NOT NULL CHECK (length(btrim("ExternalUserId")) > 0),
    "SnapshotStartedAt" timestamptz NULL,
    "VerifiedUntil" timestamptz NULL,
    "LastSuccessAt" timestamptz NULL,
    "LastAttemptAt" timestamptz NULL,
    "UpstreamAccountState" smallint NOT NULL DEFAULT 0 CHECK ("UpstreamAccountState" IN (0,1,2,3)),
    "Generation" bigint NOT NULL DEFAULT 0 CHECK ("Generation" >= 0),
    "LastErrorCode" varchar(100) NULL,
    CONSTRAINT "FK_HubIdentityState_IdentityUser" FOREIGN KEY ("ExternalIdentityId","UserId") REFERENCES "ExternalIdentity"("Id","UserId"),
    CONSTRAINT "UX_HubIdentityState_External" UNIQUE ("ConnectionId","ExternalUserId"),
    CONSTRAINT "UX_HubIdentityState_User" UNIQUE ("ConnectionId","UserId"),
    CONSTRAINT "CK_HubIdentityState_Timestamps" CHECK (
        ("SnapshotStartedAt" IS NULL OR isfinite("SnapshotStartedAt")) AND
        ("VerifiedUntil" IS NULL OR isfinite("VerifiedUntil")) AND
        ("LastSuccessAt" IS NULL OR isfinite("LastSuccessAt")) AND
        ("LastAttemptAt" IS NULL OR isfinite("LastAttemptAt"))),
    CONSTRAINT "CK_HubIdentityState_Lease" CHECK ("VerifiedUntil" IS NULL OR
        ("SnapshotStartedAt" IS NOT NULL AND "VerifiedUntil" > "SnapshotStartedAt" AND
         "VerifiedUntil" <= "SnapshotStartedAt" + interval '5 minutes')),
    CONSTRAINT "CK_HubIdentityState_Active" CHECK ("UpstreamAccountState"<>1 OR ("VerifiedUntil" IS NOT NULL AND "LastSuccessAt" IS NOT NULL))
);
CREATE INDEX "IX_HubIdentityState_User" ON "HubIdentityState"("UserId");
CREATE INDEX "IX_HubIdentityState_LastAttempt" ON "HubIdentityState"("LastAttemptAt");
CREATE FUNCTION atlas_hub_identity_state_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW."Generation"<OLD."Generation" OR NEW."ExternalIdentityId"<>OLD."ExternalIdentityId" OR
        NEW."UserId"<>OLD."UserId" OR NEW."ConnectionId"<>OLD."ConnectionId" OR NEW."ExternalUserId"<>OLD."ExternalUserId" THEN
        RAISE EXCEPTION 'Hub identity is stable and generation cannot decrease' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_HubIdentityState_Identity" BEFORE UPDATE ON "HubIdentityState"
    FOR EACH ROW EXECUTE FUNCTION atlas_hub_identity_state_guard();

CREATE VIEW "EffectiveHubIdentity" AS
SELECT s."ExternalIdentityId", s."ConnectionId", s."UserId", s."ExternalUserId", s."VerifiedUntil"
FROM "HubIdentityState" s
JOIN "ExternalIdentity" e ON e."Id"=s."ExternalIdentityId" AND e."UserId"=s."UserId" AND e."ActiveTo" IS NULL
JOIN "UserAccount" u ON u."Id"=s."UserId" AND u."IsActive"
JOIN "HubConnectionRevision" c ON c."ConnectionId"=s."ConnectionId" AND c."ActiveTo" IS NULL
WHERE c."Mode"=2 AND c."Issuer"=e."Issuer" AND s."ExternalUserId"=e."Subject"
    AND s."UpstreamAccountState"=1 AND s."SnapshotStartedAt">=c."ActiveFrom"
    AND s."SnapshotStartedAt"<=statement_timestamp() AND statement_timestamp()<s."VerifiedUntil";

CREATE VIEW "EffectiveGroupMember" AS
SELECT m."Id", m."GroupId", m."UserId", m."ActiveFrom", m."ActiveTo", h."VerifiedUntil"
FROM "GroupMember" m
LEFT JOIN "HubGroupBinding" b ON b."GroupId"=m."GroupId"
LEFT JOIN "EffectiveHubIdentity" h ON h."ConnectionId"=b."ConnectionId" AND h."UserId"=m."UserId"
WHERE m."ActiveTo" IS NULL AND (b."GroupId" IS NULL OR (b."Enabled" AND h."ExternalIdentityId" IS NOT NULL));

-- Preserve template/own-access policy from 2026092703; only the membership source changes.
CREATE OR REPLACE VIEW "EffectivePageAccess" AS
SELECT access."UserId", access."PageId", access."SpaceId", max(access."Role") AS "Role"
FROM (
    SELECT u."Id" AS "UserId", p."Id" AS "PageId", p."SpaceId", 3 AS "Role"
    FROM "UserAccount" u CROSS JOIN "Page" p
    WHERE u."IsWikiAdmin"
    UNION ALL
    SELECT g."UserId", p."Id", p."SpaceId", CASE WHEN p."IsTemplate" AND g."Role"<3 THEN 1 ELSE g."Role" END
    FROM "EffectiveSpaceGrant" g
    JOIN "Page" p ON p."SpaceId"=g."SpaceId" AND (NOT p."OwnAccess" OR g."Role"=3)
    WHERE g."UserId" IS NOT NULL
    UNION ALL
    SELECT m."UserId", p."Id", p."SpaceId", CASE WHEN p."IsTemplate" AND g."Role"<3 THEN 1 ELSE g."Role" END
    FROM "EffectiveGroupMember" m
    JOIN "EffectiveSpaceGrant" g ON g."GroupId"=m."GroupId"
    JOIN "Page" p ON p."SpaceId"=g."SpaceId" AND (NOT p."OwnAccess" OR g."Role"=3)
    UNION ALL
    SELECT g."UserId", p."Id", p."SpaceId", CASE WHEN p."IsTemplate" THEN 1 ELSE g."Role" END
    FROM "EffectiveResourceGrant" g
    JOIN "Page" p ON p."Id"=g."PageId"
    WHERE g."UserId" IS NOT NULL
    UNION ALL
    SELECT m."UserId", p."Id", p."SpaceId", CASE WHEN p."IsTemplate" THEN 1 ELSE g."Role" END
    FROM "EffectiveGroupMember" m
    JOIN "EffectiveResourceGrant" g ON g."GroupId"=m."GroupId"
    JOIN "Page" p ON p."Id"=g."PageId"
) access
JOIN "UserAccount" active ON active."Id"=access."UserId" AND active."IsActive"
GROUP BY access."UserId", access."PageId", access."SpaceId";

-- Forward-only: an old binary would treat synchronized rows as unbounded local memberships.
-- Before any downgrade, explicitly close all externally bound membership intervals.
