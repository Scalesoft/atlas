-- Text ordering of a fresh database. Equal output on postgres:18.6-alpine and on atlas-postgres proves that an existing data
-- volume keeps its collation (same C library), so its text indexes stay valid without REINDEX.
SELECT 'locale=' || datlocprovider::text || '/' || datcollate || '/' || datctype FROM pg_database WHERE datname = current_database();
SELECT 'order=' || string_agg(w, ' ' ORDER BY w)
FROM unnest(ARRAY['b', 'B', 'a', 'A', 'č', 'c', 'Č', 'ch', 'h', 'é', 'e', 'z', 'Z', '_x', '-y', '1', '10', '2', 'ä', 'ß', 'ss']) w;
