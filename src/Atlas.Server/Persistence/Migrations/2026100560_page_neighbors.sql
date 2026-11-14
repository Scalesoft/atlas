-- Atlas 2.0 V4: R1 Related pages (plan phase 2, §12; design §7.3 R1). Precomputed neighbours of every page of an index
-- generation by the cosine similarity of their centroids (the normalized mean of the page's chunk vectors). A derived,
-- rebuildable projection like the rest of the index: it holds identities and similarities only, never a title, and no
-- access decision. Every read decides visibility on the reader's current rights, the trash, templates, the current
-- publication and the AI inclusion of both spaces (PageLinkStore.RelatedAsync).
--
-- 30 neighbours are kept, so 3–5 remain after the reader's rights and the pages already linked either way are left out.
-- Templates have no centroid (title vector only, O19) and are never neighbours. Rows go with their KnowledgePage (cascade),
-- so a page that leaves the generation leaves every list at once.
CREATE TABLE "PageNeighbor" (
  "GenerationId" uuid NOT NULL,
  "PageId" uuid NOT NULL,
  "NeighborPageId" uuid NOT NULL,
  "Rank" smallint NOT NULL CONSTRAINT "CK_PageNeighbor_Rank" CHECK ("Rank" BETWEEN 1 AND 30),
  "Similarity" real NOT NULL,
  CONSTRAINT "PK_PageNeighbor" PRIMARY KEY ("GenerationId","PageId","NeighborPageId"),
  CONSTRAINT "FK_PageNeighbor_Page" FOREIGN KEY ("GenerationId","PageId")
    REFERENCES "KnowledgePage"("GenerationId","PageId") ON DELETE CASCADE,
  CONSTRAINT "FK_PageNeighbor_Neighbor" FOREIGN KEY ("GenerationId","NeighborPageId")
    REFERENCES "KnowledgePage"("GenerationId","PageId") ON DELETE CASCADE,
  CONSTRAINT "CK_PageNeighbor_NotSelf" CHECK ("PageId" <> "NeighborPageId"));
CREATE INDEX "IX_PageNeighbor_Order" ON "PageNeighbor"("GenerationId","PageId","Rank");
-- The cascade from a removed page and the incremental refresh find the lists a page appears in.
CREATE INDEX "IX_PageNeighbor_Neighbor" ON "PageNeighbor"("GenerationId","NeighborPageId");

-- When the whole generation's lists were last recomputed (maintenance): after a build completes, after activation and daily.
ALTER TABLE "IndexGeneration" ADD COLUMN "NeighborsRefreshedAt" timestamptz NULL;

-- One writer of a generation's lists at a time (the worker's store, the maintenance batches; several instances too). The
-- text key follows IAtlasLocks.AcquireAdvisoryAsync(string).
CREATE FUNCTION atlas_lock_page_neighbors(generation uuid) RETURNS void LANGUAGE sql AS $$
  SELECT pg_advisory_xact_lock(hashtextextended('page-neighbors:' || generation::text, 812)) $$;

-- The page's own 30 nearest pages of the generation by centroid (no templates, not itself), ranked by distance. HNSW with an
-- iterative scan (the generation and template filters), re-sorted outside the scan.
CREATE FUNCTION atlas_insert_own_page_neighbors(generation uuid, pages uuid[]) RETURNS void LANGUAGE sql AS $$
  INSERT INTO "PageNeighbor"("GenerationId","PageId","NeighborPageId","Rank","Similarity")
  SELECT generation, src."PageId", near."PageId",
    CAST(row_number() OVER (PARTITION BY src."PageId" ORDER BY near.d, near."PageId") AS smallint), CAST(1 - near.d AS real)
  FROM "KnowledgePage" src
  CROSS JOIN LATERAL (
    SELECT k."PageId", k."Centroid" <=> src."Centroid" AS d FROM "KnowledgePage" k
    WHERE k."GenerationId" = generation AND k."PageId" <> src."PageId" AND NOT k."IsTemplate" AND k."Centroid" IS NOT NULL
    ORDER BY k."Centroid" <=> src."Centroid" LIMIT 30) near
  WHERE src."GenerationId" = generation AND src."PageId" = ANY(pages) AND NOT src."IsTemplate" AND src."Centroid" IS NOT NULL $$;

-- After the worker stored a page (V2 StoreIndexedPage): its own list is recomputed, it leaves every other list, and it joins
-- the lists of its own neighbours where it beats their weakest entry or fills a list shorter than 30 (similarity is
-- symmetric). Lists of pages farther away keep their entries until the maintenance recomputes the generation; a list
-- that lost the page may hold 29 for that long. A template or a page without chunks ends with no list and in none.
CREATE FUNCTION atlas_refresh_page_neighbors(generation uuid, page uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  PERFORM atlas_lock_page_neighbors(generation);
  PERFORM set_config('hnsw.iterative_scan', 'relaxed_order', true);
  DELETE FROM "PageNeighbor" n WHERE n."GenerationId" = generation AND (n."PageId" = page OR n."NeighborPageId" = page);
  PERFORM atlas_insert_own_page_neighbors(generation, ARRAY[page]);
  INSERT INTO "PageNeighbor"("GenerationId","PageId","NeighborPageId","Rank","Similarity")
  SELECT generation, own."NeighborPageId", page, 30, own."Similarity"
  FROM "PageNeighbor" own
  LEFT JOIN LATERAL (
    SELECT count(*) AS n, min(o."Similarity") AS weakest FROM "PageNeighbor" o
    WHERE o."GenerationId" = generation AND o."PageId" = own."NeighborPageId") other ON true
  WHERE own."GenerationId" = generation AND own."PageId" = page AND (other.n < 30 OR own."Similarity" > other.weakest);
  -- The lists it joined: keep the best 30, renumbered by similarity.
  WITH ranked AS (
    SELECT o."PageId", o."NeighborPageId", o."Rank",
      row_number() OVER (PARTITION BY o."PageId" ORDER BY o."Similarity" DESC, o."NeighborPageId") AS r
    FROM "PageNeighbor" o
    WHERE o."GenerationId" = generation
      AND o."PageId" IN (SELECT x."PageId" FROM "PageNeighbor" x WHERE x."GenerationId" = generation AND x."NeighborPageId" = page)),
  dropped AS (
    DELETE FROM "PageNeighbor" o USING ranked x
    WHERE o."GenerationId" = generation AND o."PageId" = x."PageId" AND o."NeighborPageId" = x."NeighborPageId" AND x.r > 30)
  UPDATE "PageNeighbor" o SET "Rank" = CAST(x.r AS smallint) FROM ranked x
  WHERE o."GenerationId" = generation AND o."PageId" = x."PageId" AND o."NeighborPageId" = x."NeighborPageId"
    AND x.r <= 30 AND x."Rank" <> x.r;
END $$;

-- One batch of the maintenance's full recompute: the own lists of up to `batch` pages of the generation after `after` (by
-- Id). Returns the last page of the batch, or NULL when the generation is done, which then records the refresh. Once
-- every page's own list is recomputed, every list is exact; a batch is one short transaction.
CREATE FUNCTION atlas_refresh_generation_neighbors(generation uuid, after uuid, batch int, started timestamptz) RETURNS uuid
LANGUAGE plpgsql AS $$
DECLARE
  pages uuid[];
BEGIN
  PERFORM atlas_lock_page_neighbors(generation);
  PERFORM set_config('hnsw.iterative_scan', 'relaxed_order', true);
  SELECT array_agg(x."PageId" ORDER BY x."PageId") INTO pages FROM (
    SELECT k."PageId" FROM "KnowledgePage" k
    WHERE k."GenerationId" = generation AND (after IS NULL OR k."PageId" > after)
    ORDER BY k."PageId" LIMIT batch) x;
  IF pages IS NULL THEN
    UPDATE "IndexGeneration" SET "NeighborsRefreshedAt" = started WHERE "Id" = generation;
    RETURN NULL;
  END IF;
  DELETE FROM "PageNeighbor" n WHERE n."GenerationId" = generation AND n."PageId" = ANY(pages);
  PERFORM atlas_insert_own_page_neighbors(generation, pages);
  RETURN pages[array_length(pages, 1)];
END $$;

-- No backfill: an upgrade has no generation yet; the maintenance computes the lists of a generation once it is complete.
