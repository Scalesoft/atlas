CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS unaccent WITH SCHEMA public;

-- One row per published document, never a scan of mutable drafts or revision history.
CREATE TABLE "SearchDocument" (
  "PageId" uuid PRIMARY KEY REFERENCES "Page"("Id") ON DELETE CASCADE,
  "SpaceId" uuid NOT NULL REFERENCES "Space"("Id"),
  "RevisionId" uuid NOT NULL REFERENCES "PageRevision"("Id"),
  "Title" text NOT NULL,
  "Body" text NOT NULL,
  "NormalizedTitle" text NOT NULL,
  "NormalizedBody" text NOT NULL,
  "Vector" tsvector NOT NULL
);
CREATE INDEX "IX_SearchDocument_Vector" ON "SearchDocument" USING gin("Vector");
CREATE INDEX "IX_SearchDocument_TitleTrigram" ON "SearchDocument" USING gin("NormalizedTitle" gin_trgm_ops);
CREATE INDEX "IX_SearchDocument_BodyTrigram" ON "SearchDocument" USING gin("NormalizedBody" gin_trgm_ops);
CREATE INDEX "IX_SearchDocument_Space" ON "SearchDocument"("SpaceId");

CREATE FUNCTION atlas_refresh_search_document() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW."PublishedRevisionId" IS NULL THEN
    DELETE FROM "SearchDocument" WHERE "PageId" = NEW."Id";
  ELSE
    INSERT INTO "SearchDocument"("PageId","SpaceId","RevisionId","Title","Body","NormalizedTitle","NormalizedBody","Vector")
    SELECT NEW."Id", NEW."SpaceId", r."Id", r."Title", r."Markdown",
      lower(unaccent(r."Title")), lower(unaccent(r."Markdown")),
      setweight(to_tsvector('simple', unaccent(r."Title")), 'A') || setweight(to_tsvector('simple', unaccent(r."Markdown")), 'B')
    FROM "PageRevision" r WHERE r."Id" = NEW."PublishedRevisionId"
    ON CONFLICT ("PageId") DO UPDATE SET "SpaceId" = EXCLUDED."SpaceId", "RevisionId" = EXCLUDED."RevisionId",
      "Title" = EXCLUDED."Title", "Body" = EXCLUDED."Body", "NormalizedTitle" = EXCLUDED."NormalizedTitle",
      "NormalizedBody" = EXCLUDED."NormalizedBody", "Vector" = EXCLUDED."Vector";
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER "TR_Page_SearchDocument" AFTER INSERT OR UPDATE OF "PublishedRevisionId", "SpaceId" ON "Page"
  FOR EACH ROW EXECUTE FUNCTION atlas_refresh_search_document();

-- Existing installations are indexed in the same migration transaction.
INSERT INTO "SearchDocument"("PageId","SpaceId","RevisionId","Title","Body","NormalizedTitle","NormalizedBody","Vector")
SELECT p."Id", p."SpaceId", r."Id", r."Title", r."Markdown", lower(unaccent(r."Title")), lower(unaccent(r."Markdown")),
  setweight(to_tsvector('simple', unaccent(r."Title")), 'A') || setweight(to_tsvector('simple', unaccent(r."Markdown")), 'B')
FROM "Page" p JOIN "PageRevision" r ON r."Id" = p."PublishedRevisionId";
