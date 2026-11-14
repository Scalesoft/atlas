-- The live page channel (interaction spec §4): every saved working revision, publication change, trash and restore of a page
-- notifies atlas_page_changed with "{pageId}:working" or "{pageId}:published", whatever path wrote it (web, personal API, MCP,
-- import, restore). PostgreSQL delivers a notification only when its transaction commits, once per distinct payload; the host's
-- one listener relays it (PageChangeListener). A trash or restore is "published": it changes what every reader may see.
CREATE OR REPLACE FUNCTION atlas_notify_page_changed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_TABLE_NAME = 'PageRevision' THEN
        PERFORM pg_notify('atlas_page_changed', NEW."PageId"::text || ':working');
    ELSIF NEW."PublishedRevisionId" IS DISTINCT FROM OLD."PublishedRevisionId" OR NEW."IsTrashed" IS DISTINCT FROM OLD."IsTrashed" THEN
        PERFORM pg_notify('atlas_page_changed', NEW."Id"::text || ':published');
    END IF;
    RETURN NULL;
END $$;
CREATE OR REPLACE TRIGGER "TR_PageRevision_Changed" AFTER INSERT ON "PageRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_notify_page_changed();
CREATE OR REPLACE TRIGGER "TR_Page_Changed" AFTER UPDATE OF "PublishedRevisionId", "IsTrashed" ON "Page"
    FOR EACH ROW EXECUTE FUNCTION atlas_notify_page_changed();
