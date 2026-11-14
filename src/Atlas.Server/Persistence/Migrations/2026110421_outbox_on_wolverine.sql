-- The outbox moves onto Wolverine's durable queue "outbox" (3.3 plan §4.8, wave 4): the code sends its messages as Wolverine envelopes,
-- retries and dead letters are Wolverine's, and "OutboxMessage" goes. Its rows not delivered yet, pending or dead, become the checkpoint
-- of one queued run of the upgrade job (OutboxUpgradeJob), which sends them through the outbox once after the start; a dead one gets
-- fresh attempts. Envelopes cannot be written here: Wolverine stores them in its own binary format.
INSERT INTO "JobRun"("JobId","Trigger","Reason","State","Progress")
SELECT 'events.outbox-upgrade', 'event', 'Aktualizace na verzi 3.3', 'queued',
  jsonb_agg(jsonb_build_object('type', "MessageType", 'payload', "Payload", 'key', "OrderingKey") ORDER BY "Id")
FROM "OutboxMessage"
HAVING count(*) > 0
ON CONFLICT DO NOTHING;
DROP TABLE "OutboxMessage";

-- The catalog's full rebuild announces its resources in C# (EntityCatalogRebuildJob), so the function lists them instead of queueing.
DROP FUNCTION atlas_enqueue_entity_catalog();
CREATE FUNCTION atlas_entity_catalog_resources() RETURNS TABLE("Id" uuid, "TypeId" text) LANGUAGE sql STABLE AS $$
    SELECT r."Id", r."TypeId"
    FROM "ContentResource" r
    WHERE r."TypeId" = 'atlas.contact'
       OR r."TypeId" = 'atlas.page' AND EXISTS (SELECT 1 FROM "Page" p JOIN "PageRevision" w ON w."PageId" = p."Id"
            AND (w."ActiveTo" IS NULL OR w."Id" = p."PublishedRevisionId")
          WHERE p."Id" = r."Id" AND w."ContentJson" LIKE '%"contact%')
    ORDER BY r."Id"
$$;

-- The inbox tells a repeated delivery by Wolverine's envelope id; the outbox ids it holds mean nothing any more.
ALTER TABLE "Notification" ALTER COLUMN "LastMessageId" DROP DEFAULT, ALTER COLUMN "LastMessageId" DROP NOT NULL,
  ALTER COLUMN "LastMessageId" TYPE uuid USING NULL;
