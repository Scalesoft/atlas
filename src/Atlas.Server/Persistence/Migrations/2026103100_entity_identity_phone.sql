-- A phone in E.164 now identifies a contact without an address or an account (contacts spec 2026-10-30 §1, EntityIdentity). Only a
-- resource with a phone can change its key, so only those are announced for the catalog's rebuild, as atlas_enqueue_entity_catalog
-- does it (ContentRevised 'rebuild'); the rest keep their rows.
INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey")
SELECT 'platform', 'Atlas.Server.Platform.Events.ContentRevised, Atlas.Server',
    jsonb_build_object('resourceId', r."Id", 'contentType', r."TypeId", 'revisionKind', 'rebuild', 'revisionId', NULL), r."Id"::text
FROM "ContentResource" r
WHERE r."TypeId" = 'atlas.contact' AND r."Data" -> 'phones' IS NOT NULL
   OR r."TypeId" = 'atlas.page' AND EXISTS (SELECT 1 FROM "Page" p JOIN "PageRevision" w ON w."PageId" = p."Id"
        AND (w."ActiveTo" IS NULL OR w."Id" = p."PublishedRevisionId")
      WHERE p."Id" = r."Id" AND w."ContentJson" LIKE '%"contact%' AND w."ContentJson" LIKE '%"phones"%')
ORDER BY r."Id";
