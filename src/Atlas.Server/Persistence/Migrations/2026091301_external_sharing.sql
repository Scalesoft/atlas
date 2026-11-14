-- Existing ScaleAtlas dialect: quoted PascalCase, UUID identity, UTC ActiveFrom/ActiveTo.
-- Policy is temporal; a link deliberately pins its enabled policy revision and published content revision.
CREATE TABLE "SpaceExternalSharingPolicy" (
    "Id" uuid NOT NULL CONSTRAINT "PK_SpaceExternalSharingPolicy" PRIMARY KEY,
    "SpaceId" uuid NOT NULL,
    "Enabled" boolean NOT NULL,
    "ChangedById" uuid NOT NULL,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "FK_SpaceExternalSharingPolicy_Space" FOREIGN KEY ("SpaceId") REFERENCES "Space"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_SpaceExternalSharingPolicy_ChangedBy" FOREIGN KEY ("ChangedById") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    CONSTRAINT "UX_SpaceExternalSharingPolicy_Scope" UNIQUE ("SpaceId","Id"),
    CONSTRAINT "CK_SpaceExternalSharingPolicy_Period" CHECK ("ActiveTo" IS NULL OR "ActiveTo">"ActiveFrom"),
    CONSTRAINT "EX_SpaceExternalSharingPolicy_NoOverlap" EXCLUDE USING gist ("SpaceId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_SpaceExternalSharingPolicy_Current" ON "SpaceExternalSharingPolicy"("SpaceId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_SpaceExternalSharingPolicy_History" ON "SpaceExternalSharingPolicy"("SpaceId","ActiveFrom" DESC);
CREATE INDEX "IX_SpaceExternalSharingPolicy_ChangedBy" ON "SpaceExternalSharingPolicy"("ChangedById");

-- Immutable issuance plus terminal revocation, matching PersonalApiToken. SpaceId is original capability
-- scope, not a duplicate of the mutable Page.SpaceId. No duplicated content, author name or title.
CREATE TABLE "PageShareLink" (
    "Id" uuid NOT NULL CONSTRAINT "PK_PageShareLink" PRIMARY KEY,
    "SpaceId" uuid NOT NULL,
    "PageId" uuid NOT NULL,
    "RevisionId" uuid NOT NULL,
    "PolicyId" uuid NOT NULL,
    "CreatedById" uuid NOT NULL,
    "CreatorSecurityStamp" varchar(64) NOT NULL,
    "Name" varchar(100) NOT NULL,
    "TokenHash" varchar(64) NOT NULL,
    "ExpiresAt" timestamptz NOT NULL,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "FK_PageShareLink_Space" FOREIGN KEY ("SpaceId") REFERENCES "Space"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_PageShareLink_Page" FOREIGN KEY ("PageId") REFERENCES "Page"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_PageShareLink_Revision" FOREIGN KEY ("PageId","RevisionId") REFERENCES "PageRevision"("PageId","Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_PageShareLink_Policy" FOREIGN KEY ("SpaceId","PolicyId") REFERENCES "SpaceExternalSharingPolicy"("SpaceId","Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_PageShareLink_CreatedBy" FOREIGN KEY ("CreatedById") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    CONSTRAINT "UX_PageShareLink_TokenHash" UNIQUE ("TokenHash"),
    CONSTRAINT "CK_PageShareLink_Name" CHECK (length(btrim("Name")) BETWEEN 1 AND 100),
    CONSTRAINT "CK_PageShareLink_TokenHash" CHECK ("TokenHash" ~ '^[0-9A-F]{64}$'),
    CONSTRAINT "CK_PageShareLink_Expiry" CHECK ("ExpiresAt">"ActiveFrom" AND "ExpiresAt"<="ActiveFrom"+interval '30 days'),
    CONSTRAINT "CK_PageShareLink_Period" CHECK ("ActiveTo" IS NULL OR "ActiveTo">"ActiveFrom")
);
CREATE INDEX "IX_PageShareLink_Space" ON "PageShareLink"("SpaceId","ActiveFrom" DESC);
CREATE INDEX "IX_PageShareLink_Page" ON "PageShareLink"("PageId","ActiveFrom" DESC);
CREATE INDEX "IX_PageShareLink_Revision" ON "PageShareLink"("RevisionId");
CREATE INDEX "IX_PageShareLink_Policy" ON "PageShareLink"("PolicyId");
CREATE INDEX "IX_PageShareLink_CreatedBy" ON "PageShareLink"("CreatedById");

-- A narrow DB guard mirrors SpaceRoles for mutation-time invalidation. VOLATILE deliberately sees
-- the grant/membership change made by the current statement. Public reads also check SpaceRoles.
CREATE FUNCTION atlas_share_creator_is_admin(actor uuid, space uuid) RETURNS boolean LANGUAGE sql VOLATILE AS $$
 SELECT EXISTS (SELECT 1 FROM "UserAccount" u WHERE u."Id"=actor AND u."IsActive" AND
   (u."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "SpaceGrant" g WHERE g."SpaceId"=space AND g."Role"=3 AND g."ActiveTo" IS NULL
     AND (g."UserId"=actor OR EXISTS (SELECT 1 FROM "GroupMember" m WHERE m."UserId"=actor AND m."GroupId"=g."GroupId" AND m."ActiveTo" IS NULL)))))
$$;

-- These AFTER triggers close capabilities in the SAME transaction as the access mutation. They do
-- not acquire an advisory-lock upgrade: existing handlers hold Space/Page/account locks or the
-- global content exclusive lock, compatible with the public read guard. Rollback rolls back closure.
-- Never reopen a link; regranting access, untrashing or moving back cannot revive the old token.
CREATE FUNCTION atlas_revoke_page_shares() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW."SpaceId" IS DISTINCT FROM OLD."SpaceId" OR (NEW."IsTrashed" AND NOT OLD."IsTrashed") THEN
  UPDATE "PageShareLink" SET "ActiveTo"=greatest(clock_timestamp(),"ActiveFrom"+interval '1 microsecond')
   WHERE "PageId"=NEW."Id" AND "ActiveTo" IS NULL AND "ExpiresAt">clock_timestamp();
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER "TR_Page_RevokeShares" AFTER UPDATE ON "Page" FOR EACH ROW EXECUTE FUNCTION atlas_revoke_page_shares();

CREATE FUNCTION atlas_revoke_account_shares() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF ROW(NEW."SecurityStamp",NEW."IsActive",NEW."IsWikiAdmin") IS DISTINCT FROM ROW(OLD."SecurityStamp",OLD."IsActive",OLD."IsWikiAdmin") THEN
  UPDATE "PageShareLink" SET "ActiveTo"=greatest(clock_timestamp(),"ActiveFrom"+interval '1 microsecond')
   WHERE "CreatedById"=NEW."Id" AND "ActiveTo" IS NULL AND "ExpiresAt">clock_timestamp();
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER "TR_UserAccount_RevokeShares" AFTER UPDATE ON "UserAccount" FOR EACH ROW EXECUTE FUNCTION atlas_revoke_account_shares();

CREATE FUNCTION atlas_revoke_grant_shares() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF OLD."Role"=3 AND OLD."ActiveTo" IS NULL THEN
  UPDATE "PageShareLink" l SET "ActiveTo"=greatest(clock_timestamp(),l."ActiveFrom"+interval '1 microsecond')
   WHERE l."SpaceId"=OLD."SpaceId" AND l."ActiveTo" IS NULL AND l."ExpiresAt">clock_timestamp()
    AND (l."CreatedById"=OLD."UserId" OR EXISTS (SELECT 1 FROM "GroupMember" m
      WHERE m."GroupId"=OLD."GroupId" AND m."UserId"=l."CreatedById" AND m."ActiveTo" IS NULL))
    AND NOT atlas_share_creator_is_admin(l."CreatedById",l."SpaceId");
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER "TR_SpaceGrant_RevokeShares" AFTER UPDATE OR DELETE ON "SpaceGrant" FOR EACH ROW EXECUTE FUNCTION atlas_revoke_grant_shares();

CREATE FUNCTION atlas_revoke_membership_shares() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF OLD."ActiveTo" IS NULL THEN
  UPDATE "PageShareLink" l SET "ActiveTo"=greatest(clock_timestamp(),l."ActiveFrom"+interval '1 microsecond')
   WHERE l."CreatedById"=OLD."UserId" AND l."ActiveTo" IS NULL AND l."ExpiresAt">clock_timestamp()
    AND NOT atlas_share_creator_is_admin(l."CreatedById",l."SpaceId");
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER "TR_GroupMember_RevokeShares" AFTER UPDATE OR DELETE ON "GroupMember" FOR EACH ROW EXECUTE FUNCTION atlas_revoke_membership_shares();
