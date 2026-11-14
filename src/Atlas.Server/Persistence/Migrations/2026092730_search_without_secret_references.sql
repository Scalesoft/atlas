-- WP4 V6 with editor 0.6.0: the Markdown projection carries password references as ::secret{...} / :secret{...} directives
-- (reference attributes only, never a value). The search read models are shared by every reader, so they never keep them:
-- neither the snippet body, the normalized text and vectors, nor heading titles. PageRevision stays authoritative and keeps
-- the directives (the page source and a collaboration seed parse them). Drafts take their snippet from PageRevision; the
-- server removes directives there before the snippet leaves (SearchText.Snippet).
-- New migration only; historical function definitions remain immutable.

-- The plugin's syntax: the braces close on the same line and hold no other brace. Code is not skipped: search is lossy anyway.
CREATE FUNCTION atlas_strip_secret_directives(value text) RETURNS text LANGUAGE sql IMMUTABLE STRICT AS $$
  SELECT regexp_replace(value, '::?secret\{[^{}' || chr(10) || ']*\}', ' ', 'g')
$$;

CREATE OR REPLACE FUNCTION atlas_refresh_search_document() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW."PublishedRevisionId" IS NULL THEN
    DELETE FROM "SearchDocument" WHERE "PageId" = NEW."Id";
  ELSE
    INSERT INTO "SearchDocument"("PageId","SpaceId","RevisionId","Title","Body","NormalizedTitle","NormalizedBody","Vector")
    SELECT NEW."Id", NEW."SpaceId", r."Id", r."Title", b.body,
      atlas_normalize_search_text(r."Title"), atlas_normalize_search_text(b.body),
      setweight(to_tsvector('simple', atlas_normalize_search_text(r."Title")), 'A') ||
      setweight(to_tsvector('simple', atlas_normalize_search_text(b.body)), 'B')
    FROM "PageRevision" r CROSS JOIN LATERAL (SELECT atlas_strip_secret_directives(r."Markdown") AS body) b
    WHERE r."Id" = NEW."PublishedRevisionId"
    ON CONFLICT ("PageId") DO UPDATE SET "SpaceId" = EXCLUDED."SpaceId", "RevisionId" = EXCLUDED."RevisionId",
      "Title" = EXCLUDED."Title", "Body" = EXCLUDED."Body", "NormalizedTitle" = EXCLUDED."NormalizedTitle",
      "NormalizedBody" = EXCLUDED."NormalizedBody", "Vector" = EXCLUDED."Vector";
  END IF;
  RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION atlas_update_search_structure() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE heading jsonb; boundary tsvector := '''__scaleatlas_field_boundary__'':1'::tsvector;
BEGIN
  SELECT atlas_search_headings(atlas_strip_secret_directives(r."Markdown"),r."ContentJson"),
    COALESCE((SELECT v."ActiveFrom" FROM "PagePublication" v WHERE v."PageId"=NEW."PageId" AND v."ActiveTo" IS NULL),r."ActiveFrom")
  INTO NEW."Headings",NEW."PublishedAt" FROM "PageRevision" r WHERE r."Id"=NEW."RevisionId";
  NEW."HeadingVector" := ''::tsvector;
  FOR heading IN SELECT value FROM jsonb_array_elements(NEW."Headings") LOOP
    NEW."HeadingVector" := NEW."HeadingVector" || boundary || to_tsvector('simple',heading->>'normalizedTitle');
  END LOOP;
  NEW."HeadingVector" := ts_delete(NEW."HeadingVector",'__scaleatlas_field_boundary__');
  NEW."Vector" := ts_delete(setweight(to_tsvector('simple',NEW."NormalizedTitle"),'A') || boundary
    || setweight(NEW."HeadingVector",'B') || boundary || setweight(to_tsvector('simple',NEW."NormalizedBody"),'D'),
    '__scaleatlas_field_boundary__');
  RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION atlas_project_working_search_document() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE source "PageRevision"%ROWTYPE; heading jsonb; body text; boundary tsvector := '''__scaleatlas_field_boundary__'':1'::tsvector;
BEGIN
  SELECT * INTO source FROM "PageRevision"
    WHERE "PageId"=NEW."PageId" AND "Id"=NEW."RevisionId" AND "ActiveTo" IS NULL;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Working search projection requires the current revision of its page.' USING ERRCODE='23503';
  END IF;
  body := atlas_strip_secret_directives(source."Markdown");
  NEW."NormalizedTitle" := atlas_normalize_search_text(source."Title");
  NEW."NormalizedBody" := atlas_normalize_search_text(body);
  NEW."ChangedAt" := source."ActiveFrom";
  NEW."Headings" := atlas_search_headings(body,source."ContentJson");
  NEW."HeadingVector" := ''::tsvector;
  FOR heading IN SELECT value FROM jsonb_array_elements(NEW."Headings") LOOP
    NEW."HeadingVector" := NEW."HeadingVector" || boundary || to_tsvector('simple',heading->>'normalizedTitle');
  END LOOP;
  NEW."HeadingVector" := ts_delete(NEW."HeadingVector",'__scaleatlas_field_boundary__');
  NEW."Vector" := ts_delete(setweight(to_tsvector('simple',NEW."NormalizedTitle"),'A') || boundary
    || setweight(NEW."HeadingVector",'B') || boundary || setweight(to_tsvector('simple',NEW."NormalizedBody"),'D'),
    '__scaleatlas_field_boundary__');
  RETURN NEW;
END $$;

-- Only rows whose Markdown can hold a directive; the structure triggers recompute headings and vectors.
UPDATE "SearchDocument" SET "Body"=atlas_strip_secret_directives("Body"),
  "NormalizedBody"=atlas_normalize_search_text(atlas_strip_secret_directives("Body"))
  WHERE "Body" LIKE '%secret{%';
UPDATE "WorkingSearchDocument" w SET "RevisionId"=w."RevisionId" FROM "PageRevision" r
  WHERE r."Id"=w."RevisionId" AND r."Markdown" LIKE '%secret{%';
