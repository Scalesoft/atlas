-- Content migrations (3.2.2): a card of several contacts is split where content is read (LegacyDocumentRepair, the server's reads and
-- the editor's open) and a live document by its elected snapshotter, so the background split run of 2026103197 and its progress go.
DROP TABLE "ContactSplitRun";
