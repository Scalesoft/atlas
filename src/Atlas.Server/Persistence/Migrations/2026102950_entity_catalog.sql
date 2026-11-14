-- The entity catalog (Atlas 3 plan §14.2): typed entities read from content by C# (ContactSnapshots), one row per occurrence. A page
-- has the occurrences of its working and of its published revision; a directory contact (atlas.contact) one of its own data. Filled
-- by the ContentRevised consumer (EntityCatalogHandler), which replaces a resource's rows from its current state. Derived: not backed
-- up, rebuilt after a restore and here for existing content by enqueuing ContentRevised (atlas_enqueue_entity_catalog).
-- Space, trash, publication and rights are read live from the resource at query time, so a move or a trash never leaves a row stale.
CREATE TABLE "EntityOccurrence" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY CONSTRAINT "PK_EntityOccurrence" PRIMARY KEY,
    "ResourceId" uuid NOT NULL CONSTRAINT "FK_EntityOccurrence_Resource" REFERENCES "ContentResource"("Id") ON DELETE CASCADE,
    "RevisionKind" text NOT NULL CONSTRAINT "CK_EntityOccurrence_RevisionKind" CHECK ("RevisionKind" IN ('working','published')),
    "Kind" text NOT NULL,
    -- The merge key: email:…, user:…, directory:…, object:… or name:…|organization (EntityIdentity).
    "IdentityKey" text NOT NULL,
    "Attributes" jsonb NOT NULL CONSTRAINT "CK_EntityOccurrence_Attributes" CHECK (jsonb_typeof("Attributes") = 'object'),
    -- The heading the occurrence sits under, null for a directory contact or content before the first heading.
    "Anchor" text NULL
);
CREATE INDEX "IX_EntityOccurrence_Resource" ON "EntityOccurrence" ("ResourceId", "RevisionKind");
CREATE INDEX "IX_EntityOccurrence_Kind" ON "EntityOccurrence" ("Kind", "IdentityKey");

-- A directory contact announces a change of its data, whatever path wrote it, as pages do (2026102922). Trash and scope are read live.
CREATE OR REPLACE FUNCTION atlas_outbox_object_revised() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey") VALUES ('platform',
        'Atlas.Server.Platform.Events.ContentRevised, Atlas.Server',
        jsonb_build_object('resourceId', NEW."Id", 'contentType', NEW."TypeId", 'revisionKind', 'published', 'revisionId', NULL),
        NEW."Id"::text);
    RETURN NULL;
END $$;
CREATE OR REPLACE TRIGGER "TR_ContentResource_Outbox_Insert" AFTER INSERT ON "ContentResource"
    FOR EACH ROW WHEN (NEW."TypeId" = 'atlas.contact') EXECUTE FUNCTION atlas_outbox_object_revised();
CREATE OR REPLACE TRIGGER "TR_ContentResource_Outbox_Update" AFTER UPDATE OF "Data" ON "ContentResource"
    FOR EACH ROW WHEN (NEW."TypeId" = 'atlas.contact' AND OLD."Data" IS DISTINCT FROM NEW."Data") EXECUTE FUNCTION atlas_outbox_object_revised();

-- The full rebuild: one ContentRevised per directory contact and per page whose working or published revision holds a contact
-- component (the LIKE of SearchSql skips the rest). Used here, for the content that exists, and after every restore (InstallationModules).
CREATE OR REPLACE FUNCTION atlas_enqueue_entity_catalog() RETURNS bigint LANGUAGE sql AS $$
    WITH queued AS (
        INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey")
        SELECT 'platform', 'Atlas.Server.Platform.Events.ContentRevised, Atlas.Server',
            jsonb_build_object('resourceId', r."Id", 'contentType', r."TypeId", 'revisionKind', 'published', 'revisionId', NULL), r."Id"::text
        FROM "ContentResource" r
        WHERE r."TypeId" = 'atlas.contact'
           OR r."TypeId" = 'atlas.page' AND EXISTS (SELECT 1 FROM "Page" p JOIN "PageRevision" w ON w."PageId" = p."Id"
                AND (w."ActiveTo" IS NULL OR w."Id" = p."PublishedRevisionId")
              WHERE p."Id" = r."Id" AND w."ContentJson" LIKE '%"contact%')
        ORDER BY r."Id"
        RETURNING 1)
    SELECT count(*) FROM queued
$$;
SELECT atlas_enqueue_entity_catalog();
