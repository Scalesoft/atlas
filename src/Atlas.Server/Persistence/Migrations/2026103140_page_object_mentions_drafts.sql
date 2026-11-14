-- Usage (plan 3.1 §2.1, M5): the mention projection of 2026102840 covers the page's current working revision too, so „Použito na“
-- and the last-usage warning see a contact a draft mentions before anyone publishes it. The publication's rows stay as they were and
-- readers keep comparing SourceRevisionId with Page.PublishedRevisionId; a working revision other than the publication adds rows of
-- its own (a reader asking for drafts takes every row: the table holds no other revision). Still a projection, rebuilt below and by
-- SELECT atlas_rebuild_page_object_mentions().
ALTER TABLE "PageObjectMention" DROP CONSTRAINT "PK_PageObjectMention";
ALTER TABLE "PageObjectMention" ADD CONSTRAINT "PK_PageObjectMention" PRIMARY KEY ("SourcePageId","ObjectId","SourceRevisionId");

CREATE OR REPLACE FUNCTION atlas_refresh_page_object_mentions(page uuid) RETURNS void LANGUAGE sql AS $$
  DELETE FROM "PageObjectMention" WHERE "SourcePageId"=page;
  INSERT INTO "PageObjectMention"("SourcePageId","ObjectId","SourceRevisionId")
  SELECT DISTINCT p."Id",m,r."Id" FROM "Page" p
    JOIN "PageRevision" r ON r."PageId"=p."Id" AND (r."Id"=p."PublishedRevisionId" OR r."ActiveTo" IS NULL)
    CROSS JOIN LATERAL atlas_page_object_mentions(r."ContentJson",p."SpaceId") m
  WHERE p."Id"=page;
$$;

CREATE OR REPLACE FUNCTION atlas_rebuild_page_object_mentions() RETURNS void LANGUAGE sql AS $$
  DELETE FROM "PageObjectMention";
  INSERT INTO "PageObjectMention"("SourcePageId","ObjectId","SourceRevisionId")
  SELECT DISTINCT p."Id",m,r."Id" FROM "Page" p
    JOIN "PageRevision" r ON r."PageId"=p."Id" AND (r."Id"=p."PublishedRevisionId" OR r."ActiveTo" IS NULL)
    CROSS JOIN LATERAL atlas_page_object_mentions(r."ContentJson",p."SpaceId") m;
$$;

-- A save inserts the next working revision (SavePageHandler); a repair may rewrite a revision's document in place (only a real
-- change: an ORM update sets every column, closing a revision too). Deferred to the commit as TR_Page_PageObjectMention, so the
-- closed revision's ActiveTo is set by then.
CREATE FUNCTION atlas_page_revision_object_mentions_trigger() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM atlas_refresh_page_object_mentions(NEW."PageId");
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER "TR_PageRevision_PageObjectMention" AFTER INSERT ON "PageRevision"
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION atlas_page_revision_object_mentions_trigger();
CREATE CONSTRAINT TRIGGER "TR_PageRevision_PageObjectMention_Content" AFTER UPDATE OF "ContentJson" ON "PageRevision"
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (OLD."ContentJson" IS DISTINCT FROM NEW."ContentJson")
  EXECUTE FUNCTION atlas_page_revision_object_mentions_trigger();

SELECT atlas_rebuild_page_object_mentions();
