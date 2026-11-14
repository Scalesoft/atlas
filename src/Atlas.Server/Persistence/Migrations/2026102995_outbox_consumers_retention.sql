-- The outbox delivers each consumer on its own (OutboxDispatcher): a key's order now holds per message type (one consumer), not per
-- module, so the ordering index follows the type. A board's ContentRevised had no consumer and is no longer written; the ones still
-- waiting are dropped. Operational retention (OperationalRetention, docs/architecture.md §5.2): the board sync log keeps the last 500
-- batches of each board, which is all GetBoardOperations can read back; the batch writer keeps it so from now on.
DROP INDEX "IX_OutboxMessage_Ordering";
CREATE INDEX "IX_OutboxMessage_Ordering" ON "OutboxMessage" ("MessageType", "OrderingKey", "Id") WHERE "State" = 'pending' AND "OrderingKey" IS NOT NULL;

DELETE FROM "OutboxMessage"
WHERE "MessageType" = 'Atlas.Server.Platform.Events.ContentRevised, Atlas.Server' AND "Payload"->>'contentType' = 'atlas.board';

DELETE FROM "BoardOperation" o USING "Board" b
WHERE o."BoardId" = b."Id" AND o."Sequence" <= b."Sequence" - 500;
