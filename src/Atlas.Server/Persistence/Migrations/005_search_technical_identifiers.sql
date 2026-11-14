-- Shared bounded language aliases, applied before punctuation is discarded.
-- Deliberately STABLE, not IMMUTABLE: unaccent depends on its dictionary.
CREATE FUNCTION atlas_normalize_search_text(value text) RETURNS text LANGUAGE sql STABLE STRICT AS $$
  SELECT trim(regexp_replace(
    regexp_replace(
      regexp_replace(
        regexp_replace(
          regexp_replace(lower(public.unaccent(value)), '\mc\+\+(?!\+)', 'cplusplus', 'g'),
          '\mc#(?!#)', 'csharp', 'g'),
        '\mcpp\M', 'cplusplus', 'g'),
      '\mc[[:space:]]+sharp\M', 'csharp', 'g'),
    '[^[:alnum:]]+', ' ', 'g'))
$$;

CREATE OR REPLACE FUNCTION atlas_refresh_search_document() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW."PublishedRevisionId" IS NULL THEN
    DELETE FROM "SearchDocument" WHERE "PageId" = NEW."Id";
  ELSE
    INSERT INTO "SearchDocument"("PageId","SpaceId","RevisionId","Title","Body","NormalizedTitle","NormalizedBody","Vector")
    SELECT NEW."Id", NEW."SpaceId", r."Id", r."Title", r."Markdown",
      atlas_normalize_search_text(r."Title"), atlas_normalize_search_text(r."Markdown"),
      setweight(to_tsvector('simple', atlas_normalize_search_text(r."Title")), 'A') ||
      setweight(to_tsvector('simple', atlas_normalize_search_text(r."Markdown")), 'B')
    FROM "PageRevision" r WHERE r."Id" = NEW."PublishedRevisionId"
    ON CONFLICT ("PageId") DO UPDATE SET "SpaceId" = EXCLUDED."SpaceId", "RevisionId" = EXCLUDED."RevisionId",
      "Title" = EXCLUDED."Title", "Body" = EXCLUDED."Body", "NormalizedTitle" = EXCLUDED."NormalizedTitle",
      "NormalizedBody" = EXCLUDED."NormalizedBody", "Vector" = EXCLUDED."Vector";
  END IF;
  RETURN NEW;
END $$;

-- Rebuild existing derived rows atomically; canonical rich content is unchanged.
UPDATE "SearchDocument" SET
  "NormalizedTitle" = atlas_normalize_search_text("Title"),
  "NormalizedBody" = atlas_normalize_search_text("Body"),
  "Vector" = setweight(to_tsvector('simple', atlas_normalize_search_text("Title")), 'A') ||
             setweight(to_tsvector('simple', atlas_normalize_search_text("Body")), 'B');
