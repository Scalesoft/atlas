-- Personal items (spec 2026-10-29): a private object is its owner's and its grantees'. A personal board is a board without a
-- space (SpaceId NULL, OwnerId), outside every tree; its envelope is private. Sharing one item is a ResourceGrant on the object
-- (ObjectId, role 1 read, 2 write), with the validity, history and audit every grant has. Existing rows keep their meaning.
ALTER TABLE "Board"
    ALTER COLUMN "SpaceId" DROP NOT NULL,
    ADD COLUMN "OwnerId" uuid NULL REFERENCES "UserAccount"("Id"),
    ADD CONSTRAINT "CK_Board_Owner" CHECK (
        ("SpaceId" IS NOT NULL AND "OwnerId" IS NULL)
        OR ("SpaceId" IS NULL AND "OwnerId" IS NOT NULL AND "ParentPageId" IS NULL AND "Purpose" IS NULL));
CREATE INDEX "IX_Board_Owner" ON "Board"("OwnerId") WHERE "OwnerId" IS NOT NULL;

-- The envelope of a row without a space is private to the row's owner; a board's envelope carries its title for the lists of
-- personal items (a page has no Title column, so its envelope title stays NULL).
CREATE OR REPLACE FUNCTION atlas_track_content_resource() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    current jsonb;
BEGIN
    IF TG_OP = 'DELETE' THEN
        DELETE FROM "ContentResource" WHERE "Id" = OLD."Id";
        RETURN NULL;
    END IF;
    current := to_jsonb(NEW);
    IF TG_OP = 'INSERT' THEN
        INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","TrashedAt","Title")
        VALUES (NEW."Id", TG_ARGV[0], CASE WHEN NEW."SpaceId" IS NULL THEN 'private' ELSE 'space' END, NEW."SpaceId",
            CASE WHEN NEW."SpaceId" IS NULL THEN (current ->> 'OwnerId')::uuid END, (current ->> TG_ARGV[1])::bigint, now(),
            (current ->> TG_ARGV[2])::uuid, now(), (current ->> 'TrashedAt')::timestamptz, current ->> 'Title');
    ELSE
        UPDATE "ContentResource" SET "SpaceId" = NEW."SpaceId", "Version" = (current ->> TG_ARGV[1])::bigint,
            "TrashedAt" = (current ->> 'TrashedAt')::timestamptz, "Title" = current ->> 'Title',
            "UpdatedAt" = CASE WHEN (current ->> TG_ARGV[1]) IS DISTINCT FROM (to_jsonb(OLD) ->> TG_ARGV[1]) THEN now() ELSE "UpdatedAt" END
        WHERE "Id" = NEW."Id";
    END IF;
    RETURN NULL;
END $$;
UPDATE "ContentResource" r SET "Title" = b."Title" FROM "Board" b WHERE b."Id" = r."Id";

-- No foreign key: grants are immutable access history (closed, never deleted, referenced by the audit), and an item's permanent
-- delete closes its current grants and removes the envelope, as ObjectPurgeEvent keeps the id of what is gone.
ALTER TABLE "ResourceGrant" ADD COLUMN "ObjectId" uuid NULL;
ALTER TABLE "ResourceGrant" DROP CONSTRAINT "CK_ResourceGrant_Target";
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "CK_ResourceGrant_Target" CHECK (
    ("PageId" IS NOT NULL AND "ObjectId" IS NULL AND "VaultId" IS NULL AND "SecretId" IS NULL AND "VaultSpaceId" IS NULL) OR
    ("PageId" IS NULL AND "ObjectId" IS NULL AND "VaultId" IS NOT NULL AND "VaultSpaceId" IS NOT NULL) OR
    ("ObjectId" IS NOT NULL AND "PageId" IS NULL AND "VaultId" IS NULL AND "SecretId" IS NULL AND "VaultSpaceId" IS NULL));
ALTER TABLE "ResourceGrant" DROP CONSTRAINT "CK_ResourceGrant_Role";
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "CK_ResourceGrant_Role" CHECK (
    ("PageId" IS NOT NULL AND "Role" BETWEEN 1 AND 3) OR
    ("ObjectId" IS NOT NULL AND "Role" IN (1,2)) OR
    ("PageId" IS NULL AND "ObjectId" IS NULL AND "Role" IN (10,20,40)));
-- One current assignment of a subject per object; the subject lookups (IX_ResourceGrant_User/Group) cover every target.
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "EX_ResourceGrant_ObjectUserNoOverlap"
    EXCLUDE USING gist ("ObjectId" WITH =,"UserId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
    WHERE ("ObjectId" IS NOT NULL AND "UserId" IS NOT NULL);
ALTER TABLE "ResourceGrant" ADD CONSTRAINT "EX_ResourceGrant_ObjectGroupNoOverlap"
    EXCLUDE USING gist ("ObjectId" WITH =,"GroupId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
    WHERE ("ObjectId" IS NOT NULL AND "GroupId" IS NOT NULL);
CREATE INDEX "IX_ResourceGrant_ObjectHistory" ON "ResourceGrant"("ObjectId","UserId","GroupId","ActiveTo" DESC) WHERE "ObjectId" IS NOT NULL;

-- Appended column: the views and functions over EffectiveResourceGrant keep theirs.
CREATE OR REPLACE VIEW "EffectiveResourceGrant" AS
SELECT "Id", "PageId", "UserId", "GroupId", "Role", "ActiveFrom", "ActiveTo", "StartAt", "ExpiresAt", "ObjectId"
FROM "ResourceGrant"
WHERE "ActiveTo" IS NULL
    AND ("StartAt" IS NULL OR "StartAt" <= statement_timestamp())
    AND ("ExpiresAt" IS NULL OR statement_timestamp() < "ExpiresAt");

-- The one rule of a grantee's access to a private object: the highest role of the account's own and its groups' grants valid now
-- (a Hub group only while its membership is verified, EffectiveGroupMember); NULL for none or an inactive account. The owner and
-- the object's lifecycle are the caller's.
CREATE FUNCTION atlas_object_grant_role(object uuid, account uuid) RETURNS integer LANGUAGE sql STABLE AS $$
    SELECT max(g."Role") FROM "EffectiveResourceGrant" g
    WHERE g."ObjectId" = object
      AND EXISTS (SELECT 1 FROM "UserAccount" u WHERE u."Id" = account AND u."IsActive")
      AND (g."UserId" = account OR g."GroupId" IN (SELECT m."GroupId" FROM "EffectiveGroupMember" m WHERE m."UserId" = account));
$$;
