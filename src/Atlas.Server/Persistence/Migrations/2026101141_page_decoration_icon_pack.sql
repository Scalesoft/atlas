-- A page may take any icon of the icon pack (the whole Lucide set, about 1 800 names). The set of valid names
-- lives in the application (Platform/Validation/AppearanceCatalog.cs), as it always has for spaces; a list that
-- long in a constraint would need a migration for every Lucide update. The database keeps the shape of a name,
-- the icon-colour pair rule and the colour rule.
-- Widening varchar(32) to varchar(40), the length of "Space"."Icon", changes the catalog only. The new CHECK
-- validates the stored rows once (one row per decorated page); no row is rewritten.
-- Runs in the migration runner's transaction. Automatic downgrade is unsupported: rows may then hold names
-- longer than 32 characters or outside the former list.
ALTER TABLE "PageDecoration" ALTER COLUMN "Icon" TYPE varchar(40);
ALTER TABLE "PageDecoration" DROP CONSTRAINT "CK_PageDecoration_Selection";
ALTER TABLE "PageDecoration" ADD CONSTRAINT "CK_PageDecoration_Selection" CHECK (
  ("Icon" IS NULL AND "Color" IS NULL)
  OR ("Icon" IS NOT NULL AND "Color" IS NOT NULL
    AND "Icon" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'
    AND ("Color" IN ('blue','green','amber','coral','slate','teal','orange','rose','violet')
      OR "Color" ~ '^(red|orange|amber|lime|green|teal|cyan|blue|indigo|violet|pink|slate)-[1-5]$')
  )
);
