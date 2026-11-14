-- Atlas 2.0 R2 (Links here): PageLink is a derived, rebuildable projection of "the published revision of page S links to page
-- T". It carries identities only, never titles or content; readers decide on the current rights of the actor at read time.
-- Built like SearchDocument: a trigger on Page.PublishedRevisionId in the publishing transaction (publication, draft approval,
-- backup restore), not on saves. Trash, moves and access changes do not touch it. Not backed up: restore rebuilds it.

CREATE TABLE "PageLink" (
  "SourcePageId" uuid NOT NULL CONSTRAINT "FK_PageLink_Source" REFERENCES "Page"("Id") ON DELETE CASCADE,
  -- Deliberately no foreign key: a target may appear later (import), a missing one is filtered when read.
  "TargetPageId" uuid NOT NULL,
  -- The published revision the row came from; readers compare it with Page.PublishedRevisionId.
  "SourceRevisionId" uuid NOT NULL,
  CONSTRAINT "PK_PageLink" PRIMARY KEY ("SourcePageId","TargetPageId"),
  CONSTRAINT "CK_PageLink_NotSelf" CHECK ("SourcePageId" <> "TargetPageId"));
CREATE INDEX "IX_PageLink_Target" ON "PageLink"("TargetPageId","SourcePageId");

-- The single definition of a page link in stored rich content (ContentJson, never the lossy Markdown); the projection and the
-- publication check share it. A link is a pageReference node (attrs.id) or any string href holding #/pages/<uuid>: relative,
-- /#/..., or an absolute URL, optionally followed by ?heading=. Strict mode keeps .** from visiting array items twice; silent
-- mode skips nodes without the accessed member, as atlas_search_headings does. Values that are not a UUID are ignored.
CREATE FUNCTION atlas_page_link_targets(rich text) RETURNS SETOF uuid LANGUAGE sql IMMUTABLE AS $$
  SELECT DISTINCT lower(m)::uuid FROM (
    SELECT v #>> '{}' AS m FROM jsonb_path_query(rich::jsonb->'document',
      'strict $.** ? (@.type == "pageReference").attrs.id', '{}'::jsonb, true) v
    UNION ALL
    SELECT substring(v #>> '{}' FROM '#/pages/([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})(?:[?/#]|$)')
    FROM jsonb_path_query(rich::jsonb->'document', 'strict $.**.href ? (@.type() == "string")', '{}'::jsonb, true) v
  ) s WHERE m ~ '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$'
$$;

CREATE FUNCTION atlas_refresh_page_links(page uuid) RETURNS void LANGUAGE sql AS $$
  DELETE FROM "PageLink" WHERE "SourcePageId"=page;
  INSERT INTO "PageLink"("SourcePageId","TargetPageId","SourceRevisionId")
  SELECT p."Id",t,r."Id" FROM "Page" p JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId"
    CROSS JOIN LATERAL atlas_page_link_targets(r."ContentJson") t
  WHERE p."Id"=page AND t<>p."Id";
$$;

-- A deferred constraint trigger runs at commit, when a revision inserted after its Page row exists too (the Page→PageRevision
-- foreign key is deferred and import or restore insert in either order). It reads the committed-to-be row, not NEW.
CREATE FUNCTION atlas_page_links_trigger() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM atlas_refresh_page_links(NEW."Id");
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER "TR_Page_PageLink" AFTER INSERT OR UPDATE OF "PublishedRevisionId" ON "Page"
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION atlas_page_links_trigger();

-- Backfill and operational rebuild (SELECT atlas_rebuild_page_links()). DELETE, not TRUNCATE: a rebuild on a running
-- server must not show concurrent readers an empty table.
CREATE FUNCTION atlas_rebuild_page_links() RETURNS void LANGUAGE sql AS $$
  DELETE FROM "PageLink";
  INSERT INTO "PageLink"("SourcePageId","TargetPageId","SourceRevisionId")
  SELECT p."Id",t,r."Id" FROM "Page" p JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId"
    CROSS JOIN LATERAL atlas_page_link_targets(r."ContentJson") t WHERE t<>p."Id";
$$;
SELECT atlas_rebuild_page_links();
