ALTER TABLE "BuiltinCredential" ALTER COLUMN "PasswordHash" DROP NOT NULL;
CREATE TABLE "BuiltinAccountToken" (
 "Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL REFERENCES "BuiltinCredential"("UserId"),
 "TokenHash" bytea NOT NULL UNIQUE CHECK (octet_length("TokenHash") = 32),
 "ExpiresAt" timestamptz NOT NULL, "ConsumedAt" timestamptz NULL
);
CREATE UNIQUE INDEX "UX_BuiltinAccountToken_Pending" ON "BuiltinAccountToken"("UserId") WHERE "ConsumedAt" IS NULL;
CREATE TABLE "UserAccountRevision" (
 "Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
 "Username" varchar(100) NOT NULL, "DisplayName" varchar(200) NOT NULL,
 "IsActive" boolean NOT NULL, "IsWikiAdmin" boolean NOT NULL,
 "ChangedBy" uuid NULL REFERENCES "UserAccount"("Id"),
 "ActiveFrom" timestamptz NOT NULL, "ActiveTo" timestamptz NULL,
 CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom")
);
CREATE UNIQUE INDEX "UX_UserAccountRevision_Current" ON "UserAccountRevision"("UserId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_UserAccountRevision_History" ON "UserAccountRevision"("UserId","ActiveFrom" DESC);
ALTER TABLE "UserAccountRevision" ADD CONSTRAINT "EX_UserAccountRevision_NoOverlap" EXCLUDE USING gist ("UserId" WITH =, tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&);
INSERT INTO "UserAccountRevision" SELECT gen_random_uuid(), "Id", "Username", "DisplayName", "IsActive", "IsWikiAdmin", NULL, now(), NULL FROM "UserAccount";
CREATE FUNCTION atlas_version_user_account() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
 IF TG_OP='UPDATE' AND NEW IS NOT DISTINCT FROM OLD THEN RETURN NEW; END IF;
 SELECT "ActiveFrom" INTO previous FROM "UserAccountRevision" WHERE "UserId"=NEW."Id" AND "ActiveTo" IS NULL;
 boundary := greatest(clock_timestamp(), previous + interval '1 microsecond');
 UPDATE "UserAccountRevision" SET "ActiveTo"=boundary WHERE "UserId"=NEW."Id" AND "ActiveTo" IS NULL;
 INSERT INTO "UserAccountRevision" VALUES(gen_random_uuid(),NEW."Id",NEW."Username",NEW."DisplayName",NEW."IsActive",NEW."IsWikiAdmin",NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
 RETURN NEW;
END $$;
CREATE TRIGGER "TR_UserAccount_History" AFTER INSERT OR UPDATE ON "UserAccount" FOR EACH ROW EXECUTE FUNCTION atlas_version_user_account();
CREATE TABLE "UserGroupRevision" (
 "Id" uuid PRIMARY KEY, "GroupId" uuid NOT NULL REFERENCES "UserGroup"("Id"), "Name" varchar(200) NOT NULL,
 "ChangedBy" uuid NULL REFERENCES "UserAccount"("Id"), "ActiveFrom" timestamptz NOT NULL, "ActiveTo" timestamptz NULL,
 CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom")
);
CREATE UNIQUE INDEX "UX_UserGroupRevision_Current" ON "UserGroupRevision"("GroupId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_UserGroupRevision_History" ON "UserGroupRevision"("GroupId","ActiveFrom" DESC);
ALTER TABLE "UserGroupRevision" ADD CONSTRAINT "EX_UserGroupRevision_NoOverlap" EXCLUDE USING gist ("GroupId" WITH =, tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&);
INSERT INTO "UserGroupRevision" SELECT gen_random_uuid(), "Id", "Name", NULL, now(), NULL FROM "UserGroup";
CREATE FUNCTION atlas_version_user_group() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
 IF TG_OP='UPDATE' AND NEW IS NOT DISTINCT FROM OLD THEN RETURN NEW; END IF;
 SELECT "ActiveFrom" INTO previous FROM "UserGroupRevision" WHERE "GroupId"=NEW."Id" AND "ActiveTo" IS NULL;
 boundary := greatest(clock_timestamp(), previous + interval '1 microsecond');
 UPDATE "UserGroupRevision" SET "ActiveTo"=boundary WHERE "GroupId"=NEW."Id" AND "ActiveTo" IS NULL;
 INSERT INTO "UserGroupRevision" VALUES(gen_random_uuid(),NEW."Id",NEW."Name",NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
 RETURN NEW;
END $$;
CREATE TRIGGER "TR_UserGroup_History" AFTER INSERT OR UPDATE ON "UserGroup" FOR EACH ROW EXECUTE FUNCTION atlas_version_user_group();
ALTER TABLE "GroupMember" ADD CONSTRAINT "EX_GroupMember_NoOverlap" EXCLUDE USING gist ("GroupId" WITH =,"UserId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&);
ALTER TABLE "SpaceGrant" ADD CONSTRAINT "EX_SpaceGrant_UserNoOverlap" EXCLUDE USING gist ("SpaceId" WITH =,"UserId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&) WHERE ("UserId" IS NOT NULL);
ALTER TABLE "SpaceGrant" ADD CONSTRAINT "EX_SpaceGrant_GroupNoOverlap" EXCLUDE USING gist ("SpaceId" WITH =,"GroupId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&) WHERE ("GroupId" IS NOT NULL);

-- Fast lookup of the previous boundary when a removed relation is added again.
CREATE INDEX "IX_GroupMember_PreviousBoundary" ON "GroupMember"("GroupId","UserId","ActiveTo" DESC);
CREATE INDEX "IX_SpaceGrant_UserPreviousBoundary" ON "SpaceGrant"("SpaceId","UserId","ActiveTo" DESC) WHERE "UserId" IS NOT NULL;
CREATE INDEX "IX_SpaceGrant_GroupPreviousBoundary" ON "SpaceGrant"("SpaceId","GroupId","ActiveTo" DESC) WHERE "GroupId" IS NOT NULL;
