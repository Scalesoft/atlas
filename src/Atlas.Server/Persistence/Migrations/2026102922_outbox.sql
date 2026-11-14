-- The durable outbox (Atlas 3 plan §10, architecture kap. 5): a message is written in the transaction of the change it announces and
-- OutboxDispatcher delivers it to its Wolverine handler after the commit, at least once. The row is deleted in the handler's own
-- transaction, so only pending and dead messages stay. A pending message's NextAttemptAt is both its claim lease and its backoff.
-- Operational, not content: a restore starts it empty (BackupCoverage).
CREATE TABLE "OutboxMessage" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY CONSTRAINT "PK_OutboxMessage" PRIMARY KEY,
    "ModuleId" text NOT NULL,
    "MessageType" text NOT NULL,
    "Payload" jsonb NOT NULL,
    "OrderingKey" text NULL,
    "State" text NOT NULL DEFAULT 'pending' CONSTRAINT "CK_OutboxMessage_State" CHECK ("State" IN ('pending','dead')),
    "Attempts" integer NOT NULL DEFAULT 0,
    "NextAttemptAt" timestamptz NOT NULL DEFAULT now(),
    "FailedAt" timestamptz NULL,
    "LastError" text NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX "IX_OutboxMessage_Due" ON "OutboxMessage" ("NextAttemptAt", "Id") WHERE "State" = 'pending';
CREATE INDEX "IX_OutboxMessage_Ordering" ON "OutboxMessage" ("ModuleId", "OrderingKey", "Id") WHERE "State" = 'pending' AND "OrderingKey" IS NOT NULL;

-- ContentRevised of a page, whatever path wrote it (web, personal API, MCP, import, restore, collaboration), on the same events as
-- atlas_notify_page_changed: a new working revision, and a publication, trash or restore ("published"). Ordered per page. The type
-- name is ContentRevised's (OutboxTests pins it).
CREATE OR REPLACE FUNCTION atlas_outbox_page_revised() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_TABLE_NAME = 'PageRevision' THEN
        INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey") VALUES ('platform',
            'Atlas.Server.Platform.Events.ContentRevised, Atlas.Server',
            jsonb_build_object('resourceId', NEW."PageId", 'contentType', 'atlas.page', 'revisionKind', 'working', 'revisionId', NEW."Id"),
            NEW."PageId"::text);
    ELSIF NEW."PublishedRevisionId" IS DISTINCT FROM OLD."PublishedRevisionId" OR NEW."IsTrashed" IS DISTINCT FROM OLD."IsTrashed" THEN
        INSERT INTO "OutboxMessage"("ModuleId","MessageType","Payload","OrderingKey") VALUES ('platform',
            'Atlas.Server.Platform.Events.ContentRevised, Atlas.Server',
            jsonb_build_object('resourceId', NEW."Id", 'contentType', 'atlas.page', 'revisionKind', 'published', 'revisionId', NEW."PublishedRevisionId"),
            NEW."Id"::text);
    END IF;
    RETURN NULL;
END $$;
CREATE OR REPLACE TRIGGER "TR_PageRevision_Outbox" AFTER INSERT ON "PageRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_outbox_page_revised();
CREATE OR REPLACE TRIGGER "TR_Page_Outbox" AFTER UPDATE OF "PublishedRevisionId", "IsTrashed" ON "Page"
    FOR EACH ROW EXECUTE FUNCTION atlas_outbox_page_revised();
