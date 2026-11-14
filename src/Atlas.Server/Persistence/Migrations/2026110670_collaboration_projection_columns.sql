-- 3.6: the live state's Markdown and plain-text projections were written on every flush and never read ("" for the text). Dropping
-- them drops the CHECK of 010_collaboration.sql that names them; the same rule over the columns that stay comes back.
ALTER TABLE "PageCollaboration" DROP COLUMN "ProjectionMarkdown", DROP COLUMN "ProjectionText";
ALTER TABLE "PageCollaboration" ADD CONSTRAINT "CK_PageCollaboration_Snapshot" CHECK (
    ("State" IS NULL AND "StateSha256" IS NULL AND "ProjectionJson" IS NULL)
    OR ("State" IS NOT NULL AND "StateSha256" IS NOT NULL AND "ProjectionJson" IS NOT NULL));
