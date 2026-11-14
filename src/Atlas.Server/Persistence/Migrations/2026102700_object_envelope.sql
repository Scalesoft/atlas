-- Atlas 3 F1: ContentResource becomes the envelope of every object (F1 plan §2.1). Pages and boards stay space objects whose
-- envelope their triggers keep (version = the page's save version or the board's sequence); objects of other types are stored
-- here by the object path, with their data: backed up by Objects (package G) for those, derived (the triggers) for pages and
-- boards. The defaults keep an insert of the 2026102600 shape valid (its backfill replays).
ALTER TABLE "ContentResource"
    ALTER COLUMN "SpaceId" DROP NOT NULL,
    ADD COLUMN "Scope" varchar(16) NOT NULL DEFAULT 'space',
    ADD COLUMN "OwnerId" uuid NULL REFERENCES "UserAccount"("Id"),
    ADD COLUMN "Version" bigint NOT NULL DEFAULT 0 CONSTRAINT "CK_ContentResource_Version" CHECK ("Version" >= 0),
    ADD COLUMN "CreatedBy" uuid NULL REFERENCES "UserAccount"("Id"),
    ADD COLUMN "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN "TrashedAt" timestamptz NULL,
    -- The plain-text title (projection) of an object stored here, for the library; pages and boards keep their own titles.
    ADD COLUMN "Title" varchar(400) NULL,
    -- The data of an object stored here; null for types with tables of their own (pages, boards).
    ADD COLUMN "Data" jsonb NULL CONSTRAINT "CK_ContentResource_Data" CHECK ("Data" IS NULL OR jsonb_typeof("Data") = 'object'),
    ADD CONSTRAINT "CK_ContentResource_Scope" CHECK (
        ("Scope" = 'space' AND "SpaceId" IS NOT NULL)
        OR ("Scope" = 'wiki' AND "SpaceId" IS NULL)
        OR ("Scope" = 'private' AND "SpaceId" IS NULL AND "OwnerId" IS NOT NULL));

-- Existing rows: a page's author is its owner and it last changed with its newest revision; a board has both.
UPDATE "ContentResource" r SET "Version" = p."Version", "CreatedBy" = p."OwnerId", "TrashedAt" = p."TrashedAt",
    "UpdatedAt" = COALESCE((SELECT max(v."ActiveFrom") FROM "PageRevision" v WHERE v."PageId" = p."Id"), r."CreatedAt")
FROM "Page" p WHERE p."Id" = r."Id";
UPDATE "ContentResource" r SET "Version" = b."Sequence", "CreatedBy" = b."CreatedBy", "TrashedAt" = b."TrashedAt", "UpdatedAt" = b."UpdatedAt"
FROM "Board" b WHERE b."Id" = r."Id";

CREATE INDEX "IX_ContentResource_Library" ON "ContentResource"("TypeId", "Scope", "SpaceId", "CreatedAt" DESC, "Id") WHERE "TrashedAt" IS NULL;

-- One trigger function for every content table: the arguments name the type, the version column and the author column.
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
        INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","Version","CreatedAt","CreatedBy","UpdatedAt","TrashedAt")
        VALUES (NEW."Id", TG_ARGV[0], 'space', NEW."SpaceId", (current ->> TG_ARGV[1])::bigint, now(), (current ->> TG_ARGV[2])::uuid, now(),
            (current ->> 'TrashedAt')::timestamptz);
    ELSE
        UPDATE "ContentResource" SET "SpaceId" = NEW."SpaceId", "Version" = (current ->> TG_ARGV[1])::bigint,
            "TrashedAt" = (current ->> 'TrashedAt')::timestamptz,
            "UpdatedAt" = CASE WHEN (current ->> TG_ARGV[1]) IS DISTINCT FROM (to_jsonb(OLD) ->> TG_ARGV[1]) THEN now() ELSE "UpdatedAt" END
        WHERE "Id" = NEW."Id";
    END IF;
    RETURN NULL;
END $$;
CREATE OR REPLACE TRIGGER "TR_Page_ContentResource" AFTER INSERT OR UPDATE OF "SpaceId", "Version", "TrashedAt" OR DELETE ON "Page"
    FOR EACH ROW EXECUTE FUNCTION atlas_track_content_resource('atlas.page', 'Version', 'OwnerId');
CREATE OR REPLACE TRIGGER "TR_Board_ContentResource" AFTER INSERT OR UPDATE OF "SpaceId", "Sequence", "TrashedAt" OR DELETE ON "Board"
    FOR EACH ROW EXECUTE FUNCTION atlas_track_content_resource('atlas.board', 'Sequence', 'CreatedBy');

-- Backed up by Objects (package G): the confirmed changes of objects stored here, one per version. Idempotency of a retried
-- changeId and catching up from a version; the whole log is kept, like BoardOperation.
CREATE TABLE "ObjectChange" (
    "ObjectId" uuid NOT NULL REFERENCES "ContentResource"("Id"),
    "Version" bigint NOT NULL CONSTRAINT "CK_ObjectChange_Version" CHECK ("Version" > 0),
    "ChangeId" uuid NOT NULL,
    "AuthorId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "AppliedAt" timestamptz NOT NULL,
    "PayloadJson" jsonb NOT NULL,
    PRIMARY KEY ("ObjectId", "Version"),
    CONSTRAINT "UX_ObjectChange_Change" UNIQUE ("ObjectId", "ChangeId")
);
