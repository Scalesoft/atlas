-- Atlas 3.6, no logic in the database (plan 2026-11-04, stream S3): what these triggers derived from the rows of pages, revisions,
-- boards, tree references and spaces is written by C# in the same transaction, right after NHibernate writes the row
-- (ContentRowEffects), and by the bare SQL that writes those columns in its own statement: the content envelope, the tree's history
-- and concurrency token, a space's metadata history, the contributions, a revision's version group, the live page channel (NOTIFY
-- from C#) and a new page's place (its space FOR UPDATE, the parent check, the end of its level). The rows they kept are consistent up
-- to here and nothing is rewritten. A page inserted by bare SQL without a place now takes 1024 instead of the end of its level; an
-- entity insert gets the end of its level from ContentRowEffects, so it reaches the CHECK with 0 only if that listener is not registered.
DROP TRIGGER "TR_PageRevision_Contribution" ON "PageRevision";
DROP FUNCTION atlas_track_page_contribution();
DROP TRIGGER "TR_Page_ContentResource" ON "Page";
DROP TRIGGER "TR_Board_ContentResource" ON "Board";
DROP FUNCTION atlas_track_content_resource();
DROP TRIGGER "TR_Page_Changed" ON "Page";
DROP TRIGGER "TR_PageRevision_Changed" ON "PageRevision";
DROP FUNCTION atlas_notify_page_changed();
DROP TRIGGER "TR_PageRevision_Group" ON "PageRevision";
DROP FUNCTION atlas_page_revision_group();
DROP TRIGGER "TR_Page_PrepareOrganization" ON "Page";
DROP FUNCTION atlas_prepare_page_organization();
DROP TRIGGER "TR_Page_OrganizationHistory" ON "Page";
DROP FUNCTION atlas_version_page_organization();
DROP TRIGGER "TR_Board_Organization" ON "Board";
DROP FUNCTION atlas_version_board_organization();
DROP TRIGGER "TR_TreeReference_Organization" ON "TreeReference";
DROP FUNCTION atlas_version_tree_reference_organization();
DROP TRIGGER "TR_Space_Organization" ON "Space";
DROP FUNCTION atlas_initialize_space_organization();
DROP TRIGGER "TR_Space_MetadataHistory" ON "Space";
DROP FUNCTION atlas_version_space_metadata();
ALTER TABLE "Page" ALTER COLUMN "SortOrder" SET DEFAULT 1024;
