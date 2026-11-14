-- Atlas 3.6 (rich revision 7): no disclosure block, every heading folds. The code reads a stored disclosure repaired (LegacyDocumentRepair
-- disclosure_heading, the editor's legacyDocument.ts: a heading carrying its title, one level below the heading before it, then its body),
-- and the job pages.requirements-overview rewrites the working and published revisions in place (its own test), history stays as stored.
-- A page with unsaved live edits waits for them to be saved: a revision 7 editor cannot hold a disclosure, so a live state with one and
-- unsaved edits must be saved before the upgrade (release checklist). Only page revisions are rewritten: board blocks, notes and drafts keep the
-- stored shape and rely on the read repair alone (the editor's codec on open, BoardProps for a block's Markdown and search text,
-- RichDocument.Parse and OutboundDocument for every other server read). Queued only where such a revision holds a disclosure.
INSERT INTO "JobRun"("JobId","Trigger","Reason","State")
SELECT 'pages.requirements-overview', 'event', 'Aktualizace na verzi 3.6 (rozbalovací bloky na nadpisy)', 'queued'
WHERE EXISTS (SELECT 1 FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId"
  WHERE p."PurgedAt" IS NULL AND (r."ActiveTo" IS NULL OR r."Id" = p."PublishedRevisionId") AND strpos(r."ContentJson", '"type":"disclosure"') > 0)
ON CONFLICT DO NOTHING;
