-- The trigram word similarity (query <% text) costs ~120 ns per character of the text it reads (2026102890), but pg_trgm declares
-- word_similarity_op with the default cost of 1, as cheap as comparing two integers. The planner then tested every row with it
-- instead of asking the trigram indexes: a word no draft contains read the body of every readable draft (1 000 drafts of 5 000
-- characters: 620 ms, over the 2 s search timeout on a GitHub-hosted runner). With this cost it takes the index (14 ms; 1 000 and
-- 10 000 measured alike). No row changes. pg_dump leaves out extension members, so a database restored from a dump has the
-- default cost again until this runs again; an installation whose pg_trgm another role owns keeps the default and says so.
DO $$
BEGIN
  ALTER FUNCTION public.word_similarity_op(text, text) COST 1000;
EXCEPTION WHEN insufficient_privilege THEN
  RAISE NOTICE 'word_similarity_op keeps its default cost: pg_trgm belongs to another role';
END $$;
