-- Every C# path that writes a page's or a contact's revision announces ContentRevised itself (ContentRevisions; the coverage test
-- RevisionPublicationCoverageTests keeps the list whole), so the triggers that did it go (3.3 plan §4.8, gate B): bare SQL that writes
-- revisions from now on requests the catalog's rebuild job (entities.catalog-rebuild, a queued JobRun) instead. The messages the
-- triggers already queued stay and are delivered; nothing is rewritten.
DROP TRIGGER "TR_PageRevision_Outbox" ON "PageRevision";
DROP TRIGGER "TR_Page_Outbox" ON "Page";
DROP FUNCTION atlas_outbox_page_revised();
DROP TRIGGER "TR_ContentResource_Outbox_Insert" ON "ContentResource";
DROP TRIGGER "TR_ContentResource_Outbox_Update" ON "ContentResource";
DROP FUNCTION atlas_outbox_object_revised();
