-- GAP2-4: an archive import (WikiBackupImport) sets scaleatlas.restore for its transaction, and the publications it writes are
-- announced as ContentRevised 'rebuild' without a revision id, as the installation restore's rebuild is (migration 2026102985): the
-- mention notices record what a copied or restored page mentions and tell nobody again. Every other path stays a publication.
CREATE OR REPLACE FUNCTION atlas_outbox_page_revised() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    restoring boolean := coalesce(current_setting('scaleatlas.restore', true), '') = 'on';
BEGIN
    IF TG_TABLE_NAME = 'PageRevision' THEN
        INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey") VALUES ('platform',
            'Atlas.Server.Platform.Events.ContentRevised, Atlas.Server',
            jsonb_build_object('resourceId', NEW."PageId", 'contentType', 'atlas.page', 'revisionKind', 'working', 'revisionId', NEW."Id"),
            NEW."PageId"::text);
    ELSIF NEW."PublishedRevisionId" IS DISTINCT FROM OLD."PublishedRevisionId" OR NEW."IsTrashed" IS DISTINCT FROM OLD."IsTrashed" THEN
        INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey") VALUES ('platform',
            'Atlas.Server.Platform.Events.ContentRevised, Atlas.Server',
            jsonb_build_object('resourceId', NEW."Id", 'contentType', 'atlas.page',
                'revisionKind', CASE WHEN restoring THEN 'rebuild' ELSE 'published' END,
                'revisionId', CASE WHEN restoring THEN NULL ELSE NEW."PublishedRevisionId" END),
            NEW."Id"::text);
    END IF;
    RETURN NULL;
END $$;
