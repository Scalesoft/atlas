-- Acceptance of an atlas-postgres image (deploy/postgres/verify.sh after deploy/postgres/build.ps1): run with psql -X -At -v ON_ERROR_STOP=1
-- in a container of the image. Fails on the first broken expectation; prints key=value lines the caller compares.
CREATE EXTENSION vector;
DO $$ BEGIN
  IF ('[1,0]'::vector <=> '[0,1]') <> 1 OR ('[1,0]'::vector <=> '[2,0]') <> 0 THEN
    RAISE EXCEPTION 'cosine distance is wrong';
  END IF;
END $$;
-- What Atlas 2.0 uses: a cosine HNSW index read with an iterative scan.
BEGIN;
CREATE TEMP TABLE probe (v vector(3)) ON COMMIT DROP;
INSERT INTO probe SELECT ARRAY[cos(i), sin(i), 0.5]::vector FROM generate_series(1, 500) i;
CREATE INDEX ON probe USING hnsw (v vector_cosine_ops);
SET LOCAL hnsw.iterative_scan = relaxed_order;
SET LOCAL enable_seqscan = off;
DO $$ BEGIN
  IF (SELECT count(*) FROM (SELECT v FROM probe ORDER BY v <=> '[1,0,0.5]' LIMIT 5) nearest) <> 5 THEN
    RAISE EXCEPTION 'HNSW scan returned too few rows';
  END IF;
END $$;
COMMIT;
SELECT 'postgres=' || current_setting('server_version');
SELECT 'pgvector=' || extversion FROM pg_extension WHERE extname = 'vector';
