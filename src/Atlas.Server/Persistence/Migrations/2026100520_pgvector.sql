-- Atlas 2.0 V1: the knowledge index (V2) stores embeddings in pgvector. The extension is required, not optional (O2):
-- a server without it stops here with the upgrade procedure instead of failing later in an AI migration.
-- 0.8.0 brought iterative HNSW scans (hnsw.iterative_scan), 0.8.1 PostgreSQL 18 support.
-- Managed PostgreSQL without the right to create extensions: an administrator runs CREATE EXTENSION vector beforehand.
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'vector') THEN
    RAISE EXCEPTION 'Atlas 2.0 vyžaduje PostgreSQL s rozšířením pgvector 0.8.1 nebo novějším. Použijte image atlas-postgres nebo rozšíření nainstalujte (README, Upgrade na 2.0).'
      USING ERRCODE = '0A000';
  END IF;
END $$;
CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA public;
DO $$ BEGIN
  IF (SELECT string_to_array(extversion, '.')::int[] FROM pg_extension WHERE extname = 'vector') < ARRAY[0,8,1] THEN
    RAISE EXCEPTION 'Atlas 2.0 vyžaduje pgvector 0.8.1 nebo novější (ALTER EXTENSION vector UPDATE; README, Upgrade na 2.0).'
      USING ERRCODE = '0A000';
  END IF;
END $$;
