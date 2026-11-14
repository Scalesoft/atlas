-- An unresolved contact (several people of a name, nobody picked yet) is no person: ContactSnapshots skips it now, so the catalog's
-- rows of pages that have one are rebuilt (ContentRevised 'rebuild', as atlas_enqueue_entity_catalog announces it); the rest keep theirs.
INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey")
SELECT 'platform', 'Atlas.Server.Platform.Events.ContentRevised, Atlas.Server',
    jsonb_build_object('resourceId', r."Id", 'contentType', r."TypeId", 'revisionKind', 'rebuild', 'revisionId', NULL), r."Id"::text
FROM "ContentResource" r
WHERE r."TypeId" = 'atlas.page' AND EXISTS (SELECT 1 FROM "Page" p JOIN "PageRevision" w ON w."PageId" = p."Id"
        AND (w."ActiveTo" IS NULL OR w."Id" = p."PublishedRevisionId")
      WHERE p."Id" = r."Id" AND w."ContentJson" LIKE '%"contact%' AND w."ContentJson" LIKE '%"unresolved"%')
ORDER BY r."Id";
