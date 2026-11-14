-- The catalog's full rebuild (migration 2026102950, every restore) is no publication: it announces each resource as ContentRevised
-- 'rebuild' without a revision id, which the entity catalog rebuilds from and every consumer of publications ignores (ARCH1-12).
-- Messages already queued keep their payload; only the function changes.
CREATE OR REPLACE FUNCTION atlas_enqueue_entity_catalog() RETURNS bigint LANGUAGE sql AS $$
    WITH queued AS (
        INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey")
        SELECT 'platform', 'Atlas.Server.Platform.Events.ContentRevised, Atlas.Server',
            jsonb_build_object('resourceId', r."Id", 'contentType', r."TypeId", 'revisionKind', 'rebuild', 'revisionId', NULL), r."Id"::text
        FROM "ContentResource" r
        WHERE r."TypeId" = 'atlas.contact'
           OR r."TypeId" = 'atlas.page' AND EXISTS (SELECT 1 FROM "Page" p JOIN "PageRevision" w ON w."PageId" = p."Id"
                AND (w."ActiveTo" IS NULL OR w."Id" = p."PublishedRevisionId")
              WHERE p."Id" = r."Id" AND w."ContentJson" LIKE '%"contact%')
        ORDER BY r."Id"
        RETURNING 1)
    SELECT count(*) FROM queued
$$;
