-- Atlas 3.6: the published search document of one page as a callable function, so the requirement conversion
-- (RequirementPageConversion), which rewrites a published revision in place, refreshes it without touching the Page row (an update of
-- the row would need it FOR UPDATE, which a read that converts on open holds only FOR SHARE). The trigger function of 2026102010 now
-- calls it: one projection. In an AFTER trigger the row read here is the new one, as NEW was.
CREATE OR REPLACE FUNCTION atlas_refresh_published_search_document(page uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE published uuid; space uuid;
BEGIN
  SELECT p."PublishedRevisionId", p."SpaceId" INTO published, space FROM "Page" p WHERE p."Id" = page;
  IF published IS NULL THEN
    DELETE FROM "SearchDocument" WHERE "PageId" = page;
  ELSE
    INSERT INTO "SearchDocument"("PageId","SpaceId","RevisionId","Title","Body","NormalizedTitle","NormalizedBody","Vector")
    SELECT page, space, r."Id", r."Title", b.body,
      atlas_normalize_search_text(r."Title"), atlas_normalize_search_text(b.body),
      setweight(to_tsvector('simple', atlas_normalize_search_text(r."Title")), 'A') ||
      setweight(to_tsvector('simple', atlas_normalize_search_text(b.body)), 'B')
    FROM "PageRevision" r CROSS JOIN LATERAL (SELECT atlas_search_body(r."Markdown",r."ContentJson") AS body) b
    WHERE r."Id" = published
    ON CONFLICT ("PageId") DO UPDATE SET "SpaceId" = EXCLUDED."SpaceId", "RevisionId" = EXCLUDED."RevisionId",
      "Title" = EXCLUDED."Title", "Body" = EXCLUDED."Body", "NormalizedTitle" = EXCLUDED."NormalizedTitle",
      "NormalizedBody" = EXCLUDED."NormalizedBody", "Vector" = EXCLUDED."Vector";
  END IF;
END $$;

CREATE OR REPLACE FUNCTION atlas_refresh_search_document() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM atlas_refresh_published_search_document(NEW."Id");
  RETURN NEW;
END $$;
