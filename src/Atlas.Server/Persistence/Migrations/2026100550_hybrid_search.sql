-- Atlas 2.0 V3: hybrid tiered search (S1, S2) and "Měli jste na mysli" (S3), plan phase 2 §11, decision R4.
--
-- Today's lexical functions stay untouched: without a query vector (AI off, unavailable, over budget, prefix pass, drafts)
-- SearchHandler calls them exactly as before, so search without AI is unchanged (AI-24). With a vector the handler calls
-- atlas_search_hybrid_published or atlas_search_hybrid_all, both thin wrappers of atlas_search_hybrid below.
--
-- Ranking (R4, prototype Ranking.Tiered):
--   * today's title tiers stay on top in today's order: a lexical score >= 1500 (exact title 10 000, prefix 5 000,
--     heading 2 000, alias 1 500) ranks by that score above everything else;
--   * the rest by 1/(60 + vector rank) + 0.5/(60 + lexical rank) per page (its best chunk), a missing rank adds 0;
--   * drafts (scope all) stay lexical with 1.5/(60 + draft rank), so a strong lexical draft does not fall under a vector
--     match, and are never semantic;
--   * sort = published keeps the date first, as today; the vector only adds candidates above the threshold.
-- Window: the hybrid order covers the first 300 positions; later positions continue in today's lexical order without
-- the pages already shown, so paging never repeats a page and hasMore keeps its meaning.
--
-- Tunable constants (step M of the plan tunes them, HybridSearchTests pins them): the window 300, the vector candidates
-- 200, the similarity threshold 0.45 (O17, p_min_similarity default), the vector weight 1, the lexical weight 0.5, the
-- draft weight 1.5, the RRF constant 60, the tier boundary 1500 and the passage depth 20.
--
-- Security (spec RAG §3, §6): the vector candidates are read with the access rule of the published scope in the same
-- statement (actor active with its current stamp, EffectivePageAccess role >= 1 or WikiAdmin), the chunk bound to the live
-- page's current published revision, outside the trash, not a template, in a space included in AI (atlas_space_in_ai,
-- evaluated once per space) and of the requested generation only while it is still Active. Widening the HNSW scan
-- (SearchSql.PrepareAsync: hnsw.iterative_scan = relaxed_order, ef_search 100, max_scan_tuples 20 000) filters every
-- scanned chunk by the same rule, so a hidden page never takes a candidate slot, a position, a count or hasMore.

-- The published candidates of one hybrid search, unordered: the lexical ranking of today (its window of
-- p_lexical_take rows, ranked in today's order) joined in full with the pages of the nearest chunks.
CREATE FUNCTION atlas_search_hybrid_candidates(
  p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean,
  p_actor uuid, p_stamp text, p_space uuid, p_lexical_take integer,
  p_changed_since timestamptz, p_latest boolean,
  p_generation uuid, p_embedding vector, p_min_similarity real)
RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, changed_at timestamptz,
              lexical_score real, lexical_rank integer, vector_rank integer, chunk_id bigint, chunk_rank integer,
              semantic boolean)
LANGUAGE sql STABLE AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), access AS MATERIALIZED (
          SELECT e."PageId" FROM "EffectivePageAccess" e
          WHERE e."UserId"=p_actor AND (p_space IS NULL OR e."SpaceId"=p_space)
        ), lexical AS MATERIALIZED (
          -- Today's published ranking (migration 2026100102), its rows numbered in today's order.
          SELECT ranked.*, (row_number() OVER ())::integer AS lexical_rank FROM (
            SELECT d."PageId",d."SpaceId",d."Title",d."PublishedAt",s."Name"::text AS "SpaceName",
              CASE WHEN d."NormalizedTitle"=p_query THEN 10000
                   WHEN d."NormalizedTitle" LIKE p_prefix THEN 5000
                   WHEN d."HeadingVector" @@ to_tsquery('simple',p_fts) THEN 2000
                   WHEN d."AliasVector" @@ to_tsquery('simple',p_fts) THEN 1500
                   WHEN d."Vector" @@ to_tsquery('simple',p_fts) THEN 1000
                   WHEN p_query <% d."NormalizedTitle" THEN 500 ELSE 0 END
                + 100 * ts_rank_cd(d."Vector",to_tsquery('simple',p_fts),32)
                + 50 * word_similarity(p_query,d."NormalizedTitle") AS score
            FROM "SearchDocument" d JOIN "Space" s ON s."Id"=d."SpaceId"
              JOIN "Page" p ON p."Id"=d."PageId" CROSS JOIN actor a
            WHERE NOT p."IsTrashed" AND p."PublishedRevisionId"=d."RevisionId"
              AND (p_space IS NULL OR d."SpaceId"=p_space)
              AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access))
              AND (d."Vector" @@ to_tsquery('simple',p_fts) OR d."AliasVector" @@ to_tsquery('simple',p_fts) OR (NOT p_phrase AND d."NormalizedTitle" LIKE p_prefix)
                OR (p_fuzzy AND (p_query <% d."NormalizedTitle" OR p_query <% d."NormalizedBody")))
              AND (p_changed_since IS NULL OR d."PublishedAt">=p_changed_since)
            ORDER BY CASE WHEN p_latest THEN d."PublishedAt" END DESC NULLS LAST,score DESC,d."Title",d."PageId"
            LIMIT p_lexical_take
          ) ranked
        ), generation AS MATERIALIZED (
          -- One snapshot of the pair (spec RAG §6): the generation the query was embedded for, only while it is Active.
          SELECT g."Id" FROM "IndexGeneration" g WHERE g."Id"=p_generation AND g."State"='Active'
        ), included AS MATERIALIZED (
          SELECT s."Id" FROM "Space" s WHERE (p_space IS NULL OR s."Id"=p_space) AND atlas_space_in_ai(s."Id")
        ), nearest AS MATERIALIZED (
          -- Tunable: 200 vector candidates. Every filter is evaluated on each scanned chunk (HNSW iterative scan). They are
          -- correlated checks of the chunk's row, not joins: a join lets the planner start from the pages (it badly
          -- underestimates the chunk-to-published-revision join) and compute the distance of every chunk instead of walking
          -- the graph (measured 150 ms against 3 ms over 25 000 chunks).
          SELECT c."Id" AS chunk_id,c."PageId",c."Embedding" <=> p_embedding AS distance
          FROM "KnowledgeChunk" c
          WHERE c."GenerationId"=p_generation AND EXISTS (SELECT 1 FROM generation) AND EXISTS (SELECT 1 FROM actor)
            AND (SELECT p."PublishedRevisionId"=c."RevisionId" AND NOT p."IsTrashed" AND NOT p."IsTemplate"
                   AND p."SpaceId" = ANY (ARRAY(SELECT "Id" FROM included))
                   AND (p_changed_since IS NULL OR d."PublishedAt">=p_changed_since)
                 FROM "Page" p JOIN "SearchDocument" d ON d."PageId"=p."Id" AND d."RevisionId"=p."PublishedRevisionId"
                 WHERE p."Id"=c."PageId")
            AND ((SELECT a."IsWikiAdmin" FROM actor a) OR c."PageId" IN (SELECT "PageId" FROM access))
          ORDER BY c."Embedding" <=> p_embedding
          LIMIT 200
        ), vec AS MATERIALIZED (
          -- relaxed_order may return the scan slightly out of order: rank again by the exact distance.
          SELECT n.chunk_id,n."PageId",n.distance,(row_number() OVER (ORDER BY n.distance,n.chunk_id))::integer AS chunk_rank
          FROM nearest n WHERE 1 - n.distance >= p_min_similarity
        ), vec_pages AS MATERIALIZED (
          SELECT best.*,(row_number() OVER (ORDER BY best.distance,best."PageId"))::integer AS vector_rank
          FROM (SELECT DISTINCT ON (v."PageId") v."PageId",v.chunk_id,v.chunk_rank,v.distance FROM vec v ORDER BY v."PageId",v.distance,v.chunk_id) best
        )
        SELECT COALESCE(l."PageId",v."PageId"),COALESCE(l."SpaceId",d."SpaceId"),COALESCE(l."SpaceName",s."Name"::text),
          COALESCE(l."Title",d."Title")::text,COALESCE(l."PublishedAt",d."PublishedAt"),
          l.score::real,l.lexical_rank,v.vector_rank,v.chunk_id,v.chunk_rank,l."PageId" IS NULL
        FROM lexical l
        FULL JOIN vec_pages v ON v."PageId"=l."PageId"
        LEFT JOIN "SearchDocument" d ON l."PageId" IS NULL AND d."PageId"=v."PageId"
        LEFT JOIN "Space" s ON l."PageId" IS NULL AND s."Id"=d."SpaceId"
$$;

-- One page of a hybrid search. p_drafts adds the drafts of the working scope (atlas_search_drafts, its access rule and
-- snippets unchanged); a page whose published version and draft both match appears once, where its better entry ranks.
CREATE FUNCTION atlas_search_hybrid(
  p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean,
  p_actor uuid, p_stamp text, p_space uuid, p_take integer, p_skip integer,
  p_changed_since timestamptz, p_latest boolean,
  p_generation uuid, p_embedding vector, p_min_similarity real, p_drafts boolean)
RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, snippet text,
              changed_at timestamptz, heading_title text, heading_id text, matched_alias text, score real,
              is_draft boolean, semantic boolean, passage text, passage_heading_title text, passage_heading_id text)
LANGUAGE sql STABLE AS $$
        WITH candidate AS MATERIALIZED (
          -- Tunable: the hybrid window of 300 needs the lexical top 300; a later page needs the lexical rows up to it.
          SELECT * FROM atlas_search_hybrid_candidates(p_query,p_prefix,p_fts,p_phrase,p_fuzzy,p_actor,p_stamp,p_space,
            greatest(300,p_skip+p_take),p_changed_since,p_latest,p_generation,p_embedding,p_min_similarity)
        ), draft AS MATERIALIZED (
          -- Drafts are ordered among themselves by their own rank, so the first p_skip+p_take of them are all a page can show.
          SELECT d.*,(row_number() OVER ())::integer AS draft_rank
          FROM atlas_search_drafts(p_query,p_prefix,p_fts,p_phrase,p_fuzzy,p_actor,p_stamp,p_space,p_skip+p_take,0,p_changed_since,p_latest) d
          WHERE p_drafts
        ), entry AS MATERIALIZED (
          -- tier: today's title tiers (tunable boundary 1500); fused: weights 1 (vector), 0.5 (lexical), 1.5 (draft), k = 60.
          SELECT c.page_id,c.space_id,c.space_name,c.title,c.changed_at,c.lexical_score AS score,false AS is_draft,c.semantic,
            c.chunk_id,c.chunk_rank,NULL::text AS snippet,NULL::text AS heading_title,NULL::text AS heading_id,
            CASE WHEN c.lexical_score>=1500 THEN c.lexical_score END AS tier,
            COALESCE(1.0::float8/(60+c.vector_rank),0)+COALESCE(0.5::float8/(60+c.lexical_rank),0) AS fused,
            (c.lexical_rank<=300 OR c.vector_rank IS NOT NULL) AS in_window
          FROM candidate c
          UNION ALL
          SELECT d.page_id,d.space_id,d.space_name,d.title,d.changed_at,d.score,true,false,
            NULL::bigint,NULL::integer,d.snippet,d.heading_title,d.heading_id,
            CASE WHEN d.score>=1500 THEN d.score END,
            1.5::float8/(60+d.draft_rank),
            d.draft_rank<=300
          FROM draft d
        ), best AS MATERIALIZED (
          SELECT DISTINCT ON (e.page_id) e.* FROM entry e
          ORDER BY e.page_id,CASE WHEN p_latest THEN e.changed_at END DESC NULLS LAST,e.tier DESC NULLS LAST,e.fused DESC,e.is_draft
        ), hybrid AS MATERIALIZED (
          SELECT b.page_id,(row_number() OVER (ORDER BY CASE WHEN p_latest THEN b.changed_at END DESC NULLS LAST,
              b.tier DESC NULLS LAST,b.fused DESC,b.title,b.page_id))::integer AS position
          FROM best b WHERE b.in_window
          ORDER BY position LIMIT 300
        ), positioned AS MATERIALIZED (
          SELECT * FROM hybrid
          UNION ALL
          -- After the window: today's lexical order (atlas_search_all's merge order), never a page shown above.
          SELECT b.page_id,((SELECT count(*) FROM hybrid)+row_number() OVER (ORDER BY CASE WHEN p_latest THEN b.changed_at END DESC NULLS LAST,
              b.score DESC,b.title,b.page_id))::integer
          FROM best b WHERE b.score IS NOT NULL AND NOT EXISTS (SELECT 1 FROM hybrid h WHERE h.page_id=b.page_id)
        ), page AS MATERIALIZED (
          SELECT b.*,x.position FROM positioned x JOIN best b ON b.page_id=x.page_id
          WHERE x.position>p_skip ORDER BY x.position LIMIT p_take
        )
        SELECT r.page_id,r.space_id,r.space_name,r.title,
          CASE WHEN r.is_draft THEN r.snippet
               WHEN r.semantic THEN left(d."Body",2000)
               ELSE left(ts_headline('simple',d."Body",to_tsquery('simple',p_fts),
                 'StartSel="", StopSel="", MaxWords=35, MinWords=15, MaxFragments=1'),2000) END,
          r.changed_at,
          CASE WHEN r.is_draft THEN r.heading_title WHEN r.semantic THEN k."HeadingTitle"::text ELSE h.heading->>'title' END,
          CASE WHEN r.is_draft THEN r.heading_id WHEN r.semantic THEN k."HeadingId"::text ELSE h.heading->>'headingId' END,
          matched.alias,COALESCE(r.score,0)::real,r.is_draft,r.semantic,
          -- The passage: the page's best chunk, for a lexical page only when it is among the 20 nearest (tunable) and leads
          -- into a section. Its body without the context line "{title} › {heading}".
          CASE WHEN k."Id" IS NULL THEN NULL WHEN strpos(k."Text",E'\n')>0 THEN substr(k."Text",strpos(k."Text",E'\n')+1) ELSE '' END,
          k."HeadingTitle"::text,k."HeadingId"::text
        FROM page r
        LEFT JOIN "SearchDocument" d ON NOT r.is_draft AND d."PageId"=r.page_id
        LEFT JOIN "KnowledgeChunk" k ON k."Id"=r.chunk_id AND (r.semantic OR (r.chunk_rank<=20 AND k."HeadingId" IS NOT NULL))
        LEFT JOIN LATERAL (
          SELECT heading FROM jsonb_array_elements(d."Headings") AS heading
          WHERE NOT r.is_draft AND NOT r.semantic AND heading->>'headingId' IS NOT NULL
            AND (to_tsvector('simple',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts)
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE NOT r.is_draft AND NOT r.semantic
            AND to_tsvector('simple',atlas_normalize_search_text(alias)) @@ to_tsquery('simple',p_fts)
          ORDER BY alias LIMIT 1
        ) matched ON true
        ORDER BY r.position
$$;

-- The published scope (the web's "Publikované" and the Personal API, O11).
CREATE FUNCTION atlas_search_hybrid_published(
  p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean,
  p_actor uuid, p_stamp text, p_space uuid, p_take integer, p_skip integer,
  p_changed_since timestamptz, p_latest boolean,
  p_generation uuid, p_embedding vector, p_min_similarity real DEFAULT 0.45)
RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, snippet text,
              changed_at timestamptz, heading_title text, heading_id text, matched_alias text, score real,
              semantic boolean, passage text, passage_heading_title text, passage_heading_id text)
LANGUAGE sql STABLE AS $$
  SELECT page_id,space_id,space_name,title,snippet,changed_at,heading_title,heading_id,matched_alias,score,
    semantic,passage,passage_heading_title,passage_heading_id
  FROM atlas_search_hybrid(p_query,p_prefix,p_fts,p_phrase,p_fuzzy,p_actor,p_stamp,p_space,p_take,p_skip,
    p_changed_since,p_latest,p_generation,p_embedding,p_min_similarity,false)
$$;

-- The unified scope (the web's default): the hybrid published scope composed with today's drafts, as atlas_search_all.
CREATE FUNCTION atlas_search_hybrid_all(
  p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean,
  p_actor uuid, p_stamp text, p_space uuid, p_take integer, p_skip integer,
  p_changed_since timestamptz, p_latest boolean,
  p_generation uuid, p_embedding vector, p_min_similarity real DEFAULT 0.45)
RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, snippet text,
              changed_at timestamptz, heading_title text, heading_id text, matched_alias text,
              is_draft boolean, semantic boolean, passage text, passage_heading_title text, passage_heading_id text)
LANGUAGE sql STABLE AS $$
  SELECT page_id,space_id,space_name,title,snippet,changed_at,heading_title,heading_id,matched_alias,
    is_draft,semantic,passage,passage_heading_title,passage_heading_id
  FROM atlas_search_hybrid(p_query,p_prefix,p_fts,p_phrase,p_fuzzy,p_actor,p_stamp,p_space,p_take,p_skip,
    p_changed_since,p_latest,p_generation,p_embedding,p_min_similarity,true)
$$;

-- S3 "Měli jste na mysli" (O5): a rule-based wiki feature, no model, with or without AI. At most one published title or
-- alias (a previous published title or an import path, migration 021) of a page the actor may read, trigram-similar to
-- the normalized query (tunable 0.45; the caller sets pg_trgm.similarity_threshold to it for the TitleTrigram index) and
-- not the query itself. It corrects a misspelling: a title that contains every word of the query is what the query
-- already finds, not a correction. A readable page already titled exactly like the query is a strong result: no suggestion.
CREATE FUNCTION atlas_search_did_you_mean(p_query text, p_actor uuid, p_stamp text, p_space uuid)
RETURNS text
LANGUAGE sql STABLE AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), access AS MATERIALIZED (
          SELECT e."PageId" FROM "EffectivePageAccess" e
          WHERE e."UserId"=p_actor AND (p_space IS NULL OR e."SpaceId"=p_space)
        ), named AS MATERIALIZED (
          SELECT d."PageId",d."RevisionId",d."Title"::text AS suggestion,d."NormalizedTitle" AS normalized
          FROM "SearchDocument" d WHERE d."NormalizedTitle" % p_query
          UNION ALL
          SELECT d."PageId",d."RevisionId",alias,atlas_normalize_search_text(alias)
          FROM "SearchDocument" d CROSS JOIN LATERAL jsonb_array_elements_text(d."Aliases") AS alias
          WHERE d."Aliases"<>'[]'::jsonb
        ), readable AS MATERIALIZED (
          SELECT n.suggestion,n.normalized,similarity(n.normalized,p_query) AS similarity
          FROM named n JOIN "Page" p ON p."Id"=n."PageId" AND p."PublishedRevisionId"=n."RevisionId" CROSS JOIN actor a
          WHERE NOT p."IsTrashed" AND (p_space IS NULL OR p."SpaceId"=p_space)
            AND (a."IsWikiAdmin" OR n."PageId" IN (SELECT "PageId" FROM access))
        )
        SELECT r.suggestion FROM readable r
        WHERE r.similarity>=0.45 AND r.normalized<>p_query
          AND NOT (string_to_array(p_query,' ') <@ string_to_array(r.normalized,' '))
          AND NOT EXISTS (SELECT 1 FROM readable exact WHERE exact.normalized=p_query)
        ORDER BY r.similarity DESC,r.suggestion
        LIMIT 1
$$;
