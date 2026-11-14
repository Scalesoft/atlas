-- Comments on objects: a flat timeline per object
-- (ContentResource), backed up by the comments module; nothing comes from 2.x (Wiki.js exports have no comments).

-- The space setting "Komentáře" (on by default, so for every existing space): part of the space settings, so versioned in
-- SpaceMetadataRevision and moving the settings CAS, as PinPolicy does.
ALTER TABLE "Space" ADD COLUMN "CommentsEnabled" boolean NOT NULL DEFAULT true;
ALTER TABLE "SpaceMetadataRevision" ADD COLUMN "CommentsEnabled" boolean NOT NULL DEFAULT true;
-- The default only backfills; the trigger below names every column it writes.
ALTER TABLE "SpaceMetadataRevision" ALTER COLUMN "CommentsEnabled" DROP DEFAULT;

CREATE OR REPLACE FUNCTION atlas_version_space_metadata() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
 IF TG_OP='UPDATE' AND ROW(NEW."Name",NEW."Icon",NEW."Color",NEW."Description",NEW."PinPolicy",NEW."CommentsEnabled")
   IS NOT DISTINCT FROM ROW(OLD."Name",OLD."Icon",OLD."Color",OLD."Description",OLD."PinPolicy",OLD."CommentsEnabled") THEN RETURN NEW; END IF;
 SELECT "ActiveFrom" INTO previous FROM "SpaceMetadataRevision" WHERE "SpaceId"=NEW."Id" AND "ActiveTo" IS NULL;
 boundary := greatest(clock_timestamp(),previous + interval '1 microsecond');
 UPDATE "SpaceMetadataRevision" SET "ActiveTo"=boundary WHERE "SpaceId"=NEW."Id" AND "ActiveTo" IS NULL;
 INSERT INTO "SpaceMetadataRevision"("Id","SpaceId","Name","Icon","Color","Description","PinPolicy","CommentsEnabled","ChangedBy","ActiveFrom","ActiveTo")
 VALUES(gen_random_uuid(),NEW."Id",NEW."Name",NEW."Icon",NEW."Color",NEW."Description",NEW."PinPolicy",NEW."CommentsEnabled",
   NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
 RETURN NEW;
END $$;

-- A deleted comment keeps its row (the timeline does not lie) without its text and place. Purging the object purges its comments.
CREATE TABLE "Comment" (
    "Id" uuid PRIMARY KEY,
    "ObjectId" uuid NOT NULL REFERENCES "ContentResource"("Id") ON DELETE CASCADE,
    "AuthorId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "Body" text NOT NULL CONSTRAINT "CK_Comment_Body" CHECK (length("Body") <= 10000),
    "Anchor" varchar(200) NULL,
    "CreatedAt" timestamptz NOT NULL,
    "EditedAt" timestamptz NULL,
    "DeletedAt" timestamptz NULL,
    "DeletedById" uuid NULL REFERENCES "UserAccount"("Id"),
    CONSTRAINT "CK_Comment_Deleted" CHECK (
        ("DeletedAt" IS NULL AND "DeletedById" IS NULL AND btrim("Body") <> '')
        OR ("DeletedAt" IS NOT NULL AND "DeletedById" IS NOT NULL AND "Body" = '' AND "Anchor" IS NULL))
);
-- The timeline, newest first, deleted placeholders included (cursor CreatedAt + Id); the count and the newest live comment.
CREATE INDEX "IX_Comment_Timeline" ON "Comment"("ObjectId", "CreatedAt" DESC, "Id" DESC);
CREATE INDEX "IX_Comment_Live" ON "Comment"("ObjectId", "CreatedAt" DESC) WHERE "DeletedAt" IS NULL;

-- The accounts a comment mentions, kept for the shared inbox (roadmap 3.0-B) so it connects without a data migration.
CREATE TABLE "CommentMention" (
    "CommentId" uuid NOT NULL REFERENCES "Comment"("Id") ON DELETE CASCADE,
    "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    PRIMARY KEY ("CommentId", "UserId")
);

-- Search: one document per live comment, lexemes as the pages' (atlas_search_lexemes), rewritten by the command that writes the
-- comment in its transaction (atlas_refresh_comment_search), as boards do. The text leaves out link targets (a mention's contact
-- parameters, URLs) and keeps their labels. Derived: rebuilt after a restore.
CREATE FUNCTION atlas_comment_search_text(body text) RETURNS text LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
    SELECT regexp_replace(body, '\]\([^)]*\)', ']', 'g');
$$;
CREATE TABLE "CommentSearchDocument" (
    "CommentId" uuid PRIMARY KEY REFERENCES "Comment"("Id") ON DELETE CASCADE,
    "Vector" tsvector NOT NULL
);
CREATE INDEX "IX_CommentSearchDocument_Vector" ON "CommentSearchDocument" USING gin ("Vector");
CREATE FUNCTION atlas_refresh_comment_search(comment uuid) RETURNS void LANGUAGE sql AS $$
    DELETE FROM "CommentSearchDocument" WHERE "CommentId" = comment;
    INSERT INTO "CommentSearchDocument"("CommentId", "Vector")
    SELECT c."Id", atlas_search_lexemes(t.text, atlas_normalize_search_text(t.text))
    FROM "Comment" c CROSS JOIN LATERAL (SELECT atlas_comment_search_text(c."Body") AS text) t
    WHERE c."Id" = comment AND c."DeletedAt" IS NULL;
$$;
