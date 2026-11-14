-- A live snapshot the server refuses as page content (FlushDocumentHandler: the editor schema or the document limits) is not
-- stored, but the refusal is: its sequence and the error (status, code, message, JSON path) the save of the live document reports
-- instead of waiting for a snapshot that never comes (D-M3). The next stored snapshot or a new generation clears it. Existing rows
-- have no refusal.
ALTER TABLE "PageCollaboration" ADD COLUMN "RefusedSequence" bigint NULL;
ALTER TABLE "PageCollaboration" ADD COLUMN "Refusal" text NULL;
ALTER TABLE "PageCollaboration" ADD CONSTRAINT "CK_PageCollaboration_Refusal"
  CHECK ((("RefusedSequence" IS NULL) = ("Refusal" IS NULL)) AND ("RefusedSequence" IS NULL OR "RefusedSequence" BETWEEN "SnapshotSequence" AND "Sequence"));
