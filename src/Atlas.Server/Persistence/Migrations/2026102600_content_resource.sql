-- Atlas 3: one identity for every piece of content (boards spec §3, plan chapter 6). A page registers 1:1 (Id = Page.Id), so no
-- page data, link or history moves. The triggers keep the table, so no path that creates pages (web, personal API, MCP, imports,
-- backup restore) has to know it exists. The script is re-runnable: replaying it changes nothing.
CREATE TABLE IF NOT EXISTS "ContentResource" (
    "Id" uuid PRIMARY KEY,
    "TypeId" varchar(64) NOT NULL,
    "SpaceId" uuid NOT NULL REFERENCES "Space"("Id"),
    "CreatedAt" timestamptz NOT NULL
);

-- One function for every content table; the trigger names the type. INSERT registers, a changed SpaceId (a cross-space move)
-- follows, DELETE removes.
CREATE OR REPLACE FUNCTION atlas_track_content_resource() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO "ContentResource"("Id","TypeId","SpaceId","CreatedAt") VALUES (NEW."Id", TG_ARGV[0], NEW."SpaceId", now());
    ELSIF TG_OP = 'UPDATE' THEN
        IF NEW."SpaceId" IS DISTINCT FROM OLD."SpaceId" THEN
            UPDATE "ContentResource" SET "SpaceId" = NEW."SpaceId" WHERE "Id" = NEW."Id";
        END IF;
    ELSE
        DELETE FROM "ContentResource" WHERE "Id" = OLD."Id";
    END IF;
    RETURN NULL;
END $$;
CREATE OR REPLACE TRIGGER "TR_Page_ContentResource" AFTER INSERT OR UPDATE OF "SpaceId" OR DELETE ON "Page"
    FOR EACH ROW EXECUTE FUNCTION atlas_track_content_resource('atlas.page');

-- Existing pages, trashed ones included, in one set-based statement. A page is as old as its first revision (the index on
-- PageRevision(PageId, ActiveFrom) answers each min); a page never saved dates from this migration.
INSERT INTO "ContentResource"("Id","TypeId","SpaceId","CreatedAt")
SELECT p."Id", 'atlas.page', p."SpaceId",
       COALESCE((SELECT min(r."ActiveFrom") FROM "PageRevision" r WHERE r."PageId" = p."Id"), now())
FROM "Page" p
ON CONFLICT ("Id") DO NOTHING;
