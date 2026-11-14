-- Rebuildable, bounded aliases of published content. No unpublished title/path may enter this projection.
ALTER TABLE "SearchDocument" ADD COLUMN "Aliases" jsonb NOT NULL DEFAULT '[]';
ALTER TABLE "SearchDocument" ADD COLUMN "AliasVector" tsvector NOT NULL DEFAULT '';
CREATE INDEX "IX_SearchDocument_Aliases" ON "SearchDocument" USING gin("AliasVector") WITH (fastupdate=off);
CREATE INDEX "IX_WikiImportPage_Page" ON "WikiImportPage"("PageId");
CREATE INDEX "IX_WikiImportCheckpoint_PageRevision" ON "WikiImportCheckpoint"("ImportPageId","RevisionId");

CREATE FUNCTION atlas_update_search_aliases() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE alias_text text; normalized text; boundary tsvector := '''__scaleatlas_alias_boundary__'':1'::tsvector;
BEGIN
  NEW."Aliases" := '[]'::jsonb;
  NEW."AliasVector" := ''::tsvector;
  -- Read at most 32 recent publication records using the page/history index, never the working history.
  FOR alias_text IN
    SELECT r."Title" FROM (
      SELECT "RevisionId" FROM "PagePublication" WHERE "PageId"=NEW."PageId"
      ORDER BY "ActiveFrom" DESC LIMIT 32
    ) publication JOIN "PageRevision" r ON r."Id"=publication."RevisionId"
  LOOP
    normalized := atlas_normalize_search_text(alias_text);
    IF normalized<>'' AND normalized<>NEW."NormalizedTitle" AND NOT NEW."Aliases" @> jsonb_build_array(alias_text) THEN
      NEW."Aliases" := NEW."Aliases" || jsonb_build_array(alias_text);
      NEW."AliasVector" := NEW."AliasVector" || boundary || to_tsvector('simple',normalized);
    END IF;
  END LOOP;
  -- A newly imported draft of an already public page must not reveal its source path.
  -- Keep a path once one of its exact import checkpoints really was published, including later user edits.
  FOR alias_text IN
    SELECT m."SourcePath" FROM "WikiImportPage" m WHERE m."PageId"=NEW."PageId" AND EXISTS (
      SELECT 1 FROM "WikiImportCheckpoint" c JOIN "PagePublication" v
        ON v."PageId"=m."PageId" AND v."RevisionId"=c."RevisionId"
      WHERE c."ImportPageId"=m."Id")
    ORDER BY m."SourcePath",m."Id" LIMIT 16
  LOOP
    normalized := atlas_normalize_search_text(alias_text);
    IF normalized<>'' AND NOT NEW."Aliases" @> jsonb_build_array(alias_text) THEN
      NEW."Aliases" := NEW."Aliases" || jsonb_build_array(alias_text);
      NEW."AliasVector" := NEW."AliasVector" || boundary || to_tsvector('simple',normalized);
    END IF;
  END LOOP;
  -- Keep positional gaps after removing the unqueryable marker; quoted phrases cannot cross aliases.
  NEW."AliasVector" := ts_delete(NEW."AliasVector",'__scaleatlas_alias_boundary__');
  RETURN NEW;
END $$;
CREATE TRIGGER "TR_SearchDocument_Aliases" BEFORE INSERT OR UPDATE OF "RevisionId","Title","NormalizedTitle","Aliases"
  ON "SearchDocument" FOR EACH ROW EXECUTE FUNCTION atlas_update_search_aliases();

CREATE FUNCTION atlas_refresh_publication_aliases() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP<>'INSERT' THEN UPDATE "SearchDocument" SET "Aliases"="Aliases" WHERE "PageId"=OLD."PageId"; END IF;
  IF TG_OP<>'DELETE' THEN UPDATE "SearchDocument" SET "Aliases"="Aliases" WHERE "PageId"=NEW."PageId"; END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER "TR_PagePublication_SearchAliases" AFTER INSERT OR DELETE OR UPDATE OF "RevisionId","PageId"
  ON "PagePublication" FOR EACH ROW EXECUTE FUNCTION atlas_refresh_publication_aliases();

CREATE FUNCTION atlas_refresh_import_page_aliases() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP<>'INSERT' THEN UPDATE "SearchDocument" SET "Aliases"="Aliases" WHERE "PageId"=OLD."PageId"; END IF;
  IF TG_OP<>'DELETE' THEN UPDATE "SearchDocument" SET "Aliases"="Aliases" WHERE "PageId"=NEW."PageId"; END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER "TR_WikiImportPage_SearchAliases" AFTER INSERT OR DELETE OR UPDATE
  ON "WikiImportPage" FOR EACH ROW EXECUTE FUNCTION atlas_refresh_import_page_aliases();

CREATE FUNCTION atlas_refresh_import_checkpoint_aliases() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP<>'INSERT' THEN
    UPDATE "SearchDocument" SET "Aliases"="Aliases" WHERE "PageId" IN (SELECT "PageId" FROM "WikiImportPage" WHERE "Id"=OLD."ImportPageId");
  END IF;
  IF TG_OP<>'DELETE' THEN
    UPDATE "SearchDocument" SET "Aliases"="Aliases" WHERE "PageId" IN (SELECT "PageId" FROM "WikiImportPage" WHERE "Id"=NEW."ImportPageId");
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER "TR_WikiImportCheckpoint_SearchAliases" AFTER INSERT OR DELETE OR UPDATE OF "ImportPageId","RevisionId"
  ON "WikiImportCheckpoint" FOR EACH ROW EXECUTE FUNCTION atlas_refresh_import_checkpoint_aliases();

UPDATE "SearchDocument" SET "Aliases"="Aliases";
ANALYZE "SearchDocument";
