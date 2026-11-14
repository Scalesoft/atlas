-- Expand-only account contact metadata. No contact uniqueness: email is not an identity.
-- Downgrade requires restoring a verified backup, as with earlier schema migrations.
ALTER TABLE "UserAccount" ADD COLUMN "Email" varchar(254) NULL;
ALTER TABLE "UserAccountRevision" ADD COLUMN "Email" varchar(254) NULL;

CREATE OR REPLACE FUNCTION atlas_version_user_account() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
 IF TG_OP='UPDATE' AND NEW IS NOT DISTINCT FROM OLD THEN RETURN NEW; END IF;
 SELECT "ActiveFrom" INTO previous FROM "UserAccountRevision" WHERE "UserId"=NEW."Id" AND "ActiveTo" IS NULL;
 boundary := greatest(clock_timestamp(), previous + interval '1 microsecond');
 UPDATE "UserAccountRevision" SET "ActiveTo"=boundary WHERE "UserId"=NEW."Id" AND "ActiveTo" IS NULL;
 INSERT INTO "UserAccountRevision"
 ("Id","UserId","Username","DisplayName","IsActive","IsWikiAdmin","ChangedBy","ActiveFrom","ActiveTo","Email")
 VALUES(gen_random_uuid(),NEW."Id",NEW."Username",NEW."DisplayName",NEW."IsActive",NEW."IsWikiAdmin",
 NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL,NEW."Email");
 RETURN NEW;
END $$;

-- Immutable operational sign-in facts, retained to the latest 100 per user by the writer.
-- The cookie is the source of the current session's auth_time; these facts span devices.
CREATE TABLE "UserSignIn" (
 "Id" uuid PRIMARY KEY,
 "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
 "AuthenticatedAt" timestamptz NOT NULL,
 "AuthenticationMethod" varchar(100) NOT NULL,
 CONSTRAINT "CK_UserSignIn_Time" CHECK (isfinite("AuthenticatedAt")),
 CONSTRAINT "CK_UserSignIn_Method" CHECK (length(btrim("AuthenticationMethod")) > 0)
);
CREATE INDEX "IX_UserSignIn_UserTime" ON "UserSignIn"("UserId","AuthenticatedAt" DESC,"Id" DESC);
