-- The mention projection of 2026102840, fixed forward (review 2026-10-27 cycle 1, S6 and S7). A snapshot's objectId names a contact: an id
-- of another type's object (a page, a board) is no mention, an id of no object still is one (a purged contact, read as broken). A page
-- moved to another space re-matches its snapshots without objectId by e-mail there at once, not at its next publication. The existing
-- rows are rebuilt.
CREATE OR REPLACE FUNCTION atlas_page_object_mentions(rich text, space uuid) RETURNS SETOF uuid LANGUAGE sql STABLE AS $$
  WITH snapshot AS (
    SELECT c, CASE WHEN jsonb_typeof(c->'objectId')='string'
        AND c->>'objectId' ~ '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$'
      THEN lower(c->>'objectId')::uuid END AS named
    FROM jsonb_path_query(rich::jsonb->'document',
      'strict $.** ? (@.type == "contactCard" || @.type == "contactInline").attrs.data.contacts[*]', '{}'::jsonb, true) c
    WHERE jsonb_typeof(c)='object'
  ), matched AS (
    SELECT (SELECT CASE WHEN count(DISTINCT r."Id")=1 THEN (array_agg(r."Id"))[1] END
      FROM "ContentResource" r
      WHERE r."TypeId"='atlas.contact' AND r."TrashedAt" IS NULL
        AND (r."Scope"='wiki' OR r."Scope"='space' AND r."SpaceId"=space)
        AND EXISTS (SELECT 1 FROM jsonb_array_elements_text(CASE WHEN jsonb_typeof(r."Data"->'emails')='array' THEN r."Data"->'emails' ELSE '[]' END) e
          WHERE lower(e) IN (SELECT lower(x) FROM jsonb_array_elements_text(s.c->'emails') x))) AS id
    FROM snapshot s
    WHERE s.named IS NULL AND jsonb_typeof(s.c->'emails')='array'
  )
  SELECT DISTINCT id FROM (SELECT named AS id FROM snapshot UNION ALL SELECT id FROM matched) m
  WHERE id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "ContentResource" o WHERE o."Id"=m.id AND o."TypeId"<>'atlas.contact')
$$;

-- Deferred as TR_Page_PageObjectMention; only a real change of space (an ORM update sets every column).
CREATE CONSTRAINT TRIGGER "TR_Page_PageObjectMention_Space" AFTER UPDATE OF "SpaceId" ON "Page"
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD."SpaceId" IS DISTINCT FROM NEW."SpaceId")
  EXECUTE FUNCTION atlas_page_object_mentions_trigger();

SELECT atlas_rebuild_page_object_mentions();
