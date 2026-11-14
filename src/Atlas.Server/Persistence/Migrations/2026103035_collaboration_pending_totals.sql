-- COL2-6: every append re-read the sizes of up to 4096 log rows to bound the pending log (the updates above the snapshot) and
-- served only about ten updates a second per document. The pending count and bytes are now kept with the head: an append adds
-- its update, a stored snapshot recounts what stays above it, a reset empties both. Existing live documents start from their log.
ALTER TABLE "PageCollaboration" ADD COLUMN "PendingCount" integer NOT NULL DEFAULT 0;
ALTER TABLE "PageCollaboration" ADD COLUMN "PendingBytes" bigint NOT NULL DEFAULT 0;
UPDATE "PageCollaboration" c SET "PendingCount" = p."Count", "PendingBytes" = p."Bytes"
FROM (
  SELECT u."PageId", count(*) AS "Count", sum(octet_length(u."Payload")) AS "Bytes"
  FROM "CollaborationUpdate" u JOIN "PageCollaboration" s ON s."PageId" = u."PageId" AND s."Generation" = u."Generation"
  WHERE u."Sequence" > s."SnapshotSequence"
  GROUP BY u."PageId"
) p
WHERE c."PageId" = p."PageId";
