-- Rebuildable read model; PageRevision remains authoritative and keeps its existing history.
-- The composite FK deliberately pins the exact revision belonging to this durable page.
CREATE TABLE "WorkingSearchDocument" (
  "PageId" uuid NOT NULL,
  "RevisionId" uuid NOT NULL,
  "NormalizedTitle" text NOT NULL,
  "NormalizedBody" text NOT NULL,
  "Vector" tsvector NOT NULL,
  "HeadingVector" tsvector NOT NULL,
  "Headings" jsonb NOT NULL,
  "ChangedAt" timestamptz NOT NULL,
  CONSTRAINT "PK_WorkingSearchDocument" PRIMARY KEY ("PageId"),
  CONSTRAINT "FK_WorkingSearchDocument_Page" FOREIGN KEY ("PageId") REFERENCES "Page"("Id") ON DELETE CASCADE,
  CONSTRAINT "FK_WorkingSearchDocument_PageRevision" FOREIGN KEY ("PageId","RevisionId") REFERENCES "PageRevision"("PageId","Id") ON DELETE CASCADE,
  CONSTRAINT "CK_WorkingSearchDocument_Headings" CHECK (jsonb_typeof("Headings")='array')
);
-- PageId's primary-key index also covers the single child row for composite-FK maintenance.

CREATE FUNCTION atlas_project_working_search_document() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE source "PageRevision"%ROWTYPE; heading jsonb; boundary tsvector := '''__scaleatlas_field_boundary__'':1'::tsvector;
BEGIN
  SELECT * INTO source FROM "PageRevision"
    WHERE "PageId"=NEW."PageId" AND "Id"=NEW."RevisionId" AND "ActiveTo" IS NULL;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Working search projection requires the current revision of its page.' USING ERRCODE='23503';
  END IF;
  NEW."NormalizedTitle" := atlas_normalize_search_text(source."Title");
  NEW."NormalizedBody" := atlas_normalize_search_text(source."Markdown");
  NEW."ChangedAt" := source."ActiveFrom";
  NEW."Headings" := atlas_search_headings(source."Markdown",source."ContentJson");
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
CREATE TRIGGER "TR_WorkingSearchDocument_Project" BEFORE INSERT OR UPDATE ON "WorkingSearchDocument"
  FOR EACH ROW EXECUTE FUNCTION atlas_project_working_search_document();

CREATE FUNCTION atlas_refresh_working_search_document() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW."ActiveTo" IS NULL THEN
    INSERT INTO "WorkingSearchDocument"("PageId","RevisionId") VALUES(NEW."PageId",NEW."Id")
      ON CONFLICT ("PageId") DO UPDATE SET "RevisionId"=EXCLUDED."RevisionId";
  ELSE
    DELETE FROM "WorkingSearchDocument" WHERE "PageId"=NEW."PageId" AND "RevisionId"=NEW."Id";
  END IF;
  RETURN NEW;
END $$;
-- Closing and replacing a source revision updates the projection in that same transaction.
-- Physical deletion is handled by the explicitly cascading pinned-revision/page foreign keys.
CREATE TRIGGER "TR_PageRevision_WorkingSearch" AFTER INSERT OR UPDATE OF "ActiveTo","ActiveFrom","Title","Markdown","ContentJson" ON "PageRevision"
  FOR EACH ROW EXECUTE FUNCTION atlas_refresh_working_search_document();

INSERT INTO "WorkingSearchDocument"("PageId","RevisionId")
  SELECT "PageId","Id" FROM "PageRevision" WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_WorkingSearchDocument_Vector" ON "WorkingSearchDocument" USING gin("Vector") WITH (fastupdate=off);
CREATE INDEX "IX_WorkingSearchDocument_TitleTrigram" ON "WorkingSearchDocument" USING gin("NormalizedTitle" gin_trgm_ops) WITH (fastupdate=off);
CREATE INDEX "IX_WorkingSearchDocument_BodyTrigram" ON "WorkingSearchDocument" USING gin("NormalizedBody" gin_trgm_ops) WITH (fastupdate=off);
CREATE INDEX "IX_WorkingSearchDocument_ChangedAt" ON "WorkingSearchDocument"("ChangedAt" DESC);
ANALYZE "WorkingSearchDocument";
