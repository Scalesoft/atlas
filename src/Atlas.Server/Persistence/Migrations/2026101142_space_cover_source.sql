-- The image of a space gets a source. Expand, backfill, tighten in
-- one file: the table holds one row per change of a space's image, so the whole step is one short transaction and needs no
-- NOT VALID stage.
-- Source is the discriminator of a row: Upload has the four file columns, Catalog names a drawing of the built-in catalog
-- (CoverCatalog), None is "no image by choice", Auto lets the server pick from the name, description and icon on every read and
-- is never stored as a resolved key. It repeats what FileId and CatalogKey say for Upload and Catalog; CK_SpaceCover_Shape keeps
-- them in step, and Source alone tells None from Auto. No current row still means Auto.
ALTER TABLE "SpaceCover" ADD COLUMN "Source" varchar(16) NULL;
ALTER TABLE "SpaceCover" ADD COLUMN "CatalogKey" varchar(64) NULL
  CONSTRAINT "CK_SpaceCover_CatalogKey" CHECK ("CatalogKey" ~ '^[a-z0-9]+(-[a-z0-9]+)*$');
-- Backfill, history included: a row with a file was an upload; a row without one was a removal, which meant "back to the
-- default", and the default is now the automatic pick.
UPDATE "SpaceCover" SET "Source" = CASE WHEN "FileId" IS NOT NULL THEN 'Upload' ELSE 'Auto' END;
ALTER TABLE "SpaceCover" ALTER COLUMN "Source" SET NOT NULL;
ALTER TABLE "SpaceCover" ADD CONSTRAINT "CK_SpaceCover_Source" CHECK ("Source" IN ('Auto','Catalog','Upload','None'));
ALTER TABLE "SpaceCover" ADD CONSTRAINT "CK_SpaceCover_Shape"
  CHECK ((("Source"='Upload') = ("FileId" IS NOT NULL)) AND (("Source"='Catalog') = ("CatalogKey" IS NOT NULL)));
