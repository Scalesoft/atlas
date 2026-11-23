-- Text ordering of a fresh database. Equal output on the base postgres:<version>-alpine and on atlas-postgres proves the same
-- C library (musl) and so the same order as the released images before (A B Z _x a b ... Č č; glibc sorts a A ä b B ...).
SELECT 'locale=' || datlocprovider::text || '/' || datcollate || '/' || datctype FROM pg_database WHERE datname = current_database();
SELECT 'order=' || string_agg(w, ' ' ORDER BY w)
FROM unnest(ARRAY['b', 'B', 'a', 'A', 'č', 'c', 'Č', 'ch', 'h', 'é', 'e', 'z', 'Z', '_x', '-y', '1', '10', '2', 'ä', 'ß', 'ss']) w;
