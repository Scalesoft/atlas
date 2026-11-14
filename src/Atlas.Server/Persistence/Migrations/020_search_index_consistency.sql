-- Prefer predictable interactive reads over buffered bulk-index throughput.
-- Turning fastupdate off does not flush existing entries; drain all three lists too.
ALTER INDEX "IX_SearchDocument_Vector" SET (fastupdate = off);
ALTER INDEX "IX_SearchDocument_TitleTrigram" SET (fastupdate = off);
ALTER INDEX "IX_SearchDocument_BodyTrigram" SET (fastupdate = off);
SELECT gin_clean_pending_list('"IX_SearchDocument_Vector"'::regclass);
SELECT gin_clean_pending_list('"IX_SearchDocument_TitleTrigram"'::regclass);
SELECT gin_clean_pending_list('"IX_SearchDocument_BodyTrigram"'::regclass);
ANALYZE "SearchDocument";
