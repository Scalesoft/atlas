-- Atlas 3 relations (spec 2026-10-27 §13.1 point 2): a contact mentioned in the text of a page is a relation "page → contact".
-- PageObjectMention is a derived, rebuildable projection of the published revision, built exactly like PageLink (2026100420): a
-- deferred trigger on Page.PublishedRevisionId, identities only, readers decide on the actor's current rights. Not backed up: the
-- pages' rebuild after a restore touches the published pointer and so rebuilds it.

CREATE TABLE "PageObjectMention" (
  "SourcePageId" uuid NOT NULL CONSTRAINT "FK_PageObjectMention_Source" REFERENCES "Page"("Id") ON DELETE CASCADE,
  -- Deliberately no foreign key, as PageLink's target: a trashed or purged contact is filtered (or shown broken) when read.
  "ObjectId" uuid NOT NULL,
  -- The published revision the row came from; readers compare it with Page.PublishedRevisionId.
  "SourceRevisionId" uuid NOT NULL,
  CONSTRAINT "PK_PageObjectMention" PRIMARY KEY ("SourcePageId","ObjectId"));
CREATE INDEX "IX_PageObjectMention_Object" ON "PageObjectMention"("ObjectId","SourcePageId");

-- The contacts a stored rich document mentions: every snapshot of a contactCard or contactInline node (attrs.data.contacts[*]).
-- A snapshot names its contact by objectId (the atlas.contact envelope, §13.3). An older snapshot without one is matched by e-mail,
-- ignoring case, only when exactly one live contact of the wiki or of the page's space has one of its addresses; anything else is
-- no mention. Strict mode and silent errors as atlas_page_link_targets.
-- shortcut: an e-mail match is decided at publication; a contact created, changed or trashed later counts with the page's next
-- publication or a rebuild (SELECT atlas_rebuild_page_object_mentions()).
CREATE FUNCTION atlas_page_object_mentions(rich text, space uuid) RETURNS SETOF uuid LANGUAGE sql STABLE AS $$
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
  SELECT DISTINCT id FROM (SELECT named AS id FROM snapshot UNION ALL SELECT id FROM matched) m WHERE id IS NOT NULL
$$;

CREATE FUNCTION atlas_refresh_page_object_mentions(page uuid) RETURNS void LANGUAGE sql AS $$
  DELETE FROM "PageObjectMention" WHERE "SourcePageId"=page;
  INSERT INTO "PageObjectMention"("SourcePageId","ObjectId","SourceRevisionId")
  SELECT p."Id",m,r."Id" FROM "Page" p JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId"
    CROSS JOIN LATERAL atlas_page_object_mentions(r."ContentJson",p."SpaceId") m
  WHERE p."Id"=page;
$$;

-- Deferred to the commit, as TR_Page_PageLink: the revision inserted after its Page row exists by then.
CREATE FUNCTION atlas_page_object_mentions_trigger() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM atlas_refresh_page_object_mentions(NEW."Id");
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER "TR_Page_PageObjectMention" AFTER INSERT OR UPDATE OF "PublishedRevisionId" ON "Page"
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION atlas_page_object_mentions_trigger();

-- Backfill and operational rebuild. DELETE, not TRUNCATE: concurrent readers never see an empty table.
CREATE FUNCTION atlas_rebuild_page_object_mentions() RETURNS void LANGUAGE sql AS $$
  DELETE FROM "PageObjectMention";
  INSERT INTO "PageObjectMention"("SourcePageId","ObjectId","SourceRevisionId")
  SELECT p."Id",m,r."Id" FROM "Page" p JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId"
    CROSS JOIN LATERAL atlas_page_object_mentions(r."ContentJson",p."SpaceId") m;
$$;
SELECT atlas_rebuild_page_object_mentions();
