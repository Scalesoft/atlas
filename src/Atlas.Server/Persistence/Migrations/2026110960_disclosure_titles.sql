-- Atlas 3.6 (rich revision 6): a disclosure's title is its first child `disclosureTitle`, text written in place, not the `title`
-- attribute. The code reads the stored shape repaired (LegacyDocumentRepair disclosure_title, the editor's legacyDocument.ts), and the job
-- pages.requirements-overview rewrites the working and published revisions in place (its own test), history as stored; a page with live
-- edits waits and is repaired on open. Queued only where such a revision holds a disclosure with its title attribute.
-- Only page revisions are rewritten: board blocks, notes and drafts keep the stored shape and rely on the read repair alone (the editor's
-- codec on open, BoardProps for a block's Markdown and search text, RichDocument.Parse for every other server read).
INSERT INTO "JobRun"("JobId","Trigger","Reason","State")
SELECT 'pages.requirements-overview', 'event', 'Aktualizace na verzi 3.6 (názvy rozbalovacích bloků)', 'queued'
WHERE EXISTS (SELECT 1 FROM "PageRevision" r JOIN "Page" p ON p."Id" = r."PageId"
  WHERE p."PurgedAt" IS NULL AND (r."ActiveTo" IS NULL OR r."Id" = p."PublishedRevisionId")
    AND strpos(r."ContentJson", '"type":"disclosure","attrs":{"title":"') > 0)
ON CONFLICT DO NOTHING;
