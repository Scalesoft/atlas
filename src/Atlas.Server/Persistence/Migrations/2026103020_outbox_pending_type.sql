-- The fan-out (OutboxDispatcher.PrepareAsync) runs before every claimed batch and looks for pending event rows by type; no index
-- served it (IX_OutboxMessage_Ordering holds keyed rows only), so a backlog of N deliveries cost N/50 sequential scans of the queue
-- under the fan-out lock (PER3-3). The claim and the coalescing read pending rows by type as well.
CREATE INDEX "IX_OutboxMessage_PendingType" ON "OutboxMessage" ("MessageType", "Id") WHERE "State" = 'pending';
