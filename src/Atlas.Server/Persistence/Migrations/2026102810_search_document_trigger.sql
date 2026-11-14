-- Review P2-9: NHibernate writes every column of a page it saves, so "UPDATE OF PublishedRevisionId, SpaceId" fired the search
-- document's projection on every rename, move within the space and access change (160-195 ms for a 500 KB page). It now runs only
-- when one of them really changes, or when a restore asks for every document again (scaleatlas.reindex, InstallationModules).
-- Existing documents stay as they are: the projection they hold is the one the function writes.
DROP TRIGGER "TR_Page_SearchDocument" ON "Page";
CREATE TRIGGER "TR_Page_SearchDocument" AFTER INSERT ON "Page"
  FOR EACH ROW EXECUTE FUNCTION atlas_refresh_search_document();
CREATE TRIGGER "TR_Page_SearchDocumentChange" AFTER UPDATE OF "PublishedRevisionId", "SpaceId" ON "Page"
  FOR EACH ROW WHEN (OLD."PublishedRevisionId" IS DISTINCT FROM NEW."PublishedRevisionId" OR OLD."SpaceId" IS DISTINCT FROM NEW."SpaceId"
    OR current_setting('scaleatlas.reindex', true) = 'on')
  EXECUTE FUNCTION atlas_refresh_search_document();
