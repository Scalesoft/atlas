-- The alias tier of 2026102720 (an earlier page title ranks above a heading inside a page, 2500) over the bodies of
-- 2026102873 (search latency), which redefined the published searches with the old 1500 when both lines met in Atlas 3.
-- The published searches are 2026102873's with only that number changed; the hybrid candidates are 2026102720's, for the
-- databases that recorded 2026102720 before it was the alias migration. New migration only; historical definitions remain immutable.

CREATE OR REPLACE FUNCTION atlas_search_published(
  p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean,
  p_actor uuid, p_stamp text, p_space uuid, p_take integer, p_skip integer)
RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, snippet text,
              changed_at timestamptz, heading_title text, heading_id text, matched_alias text)
LANGUAGE sql STABLE AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), access AS MATERIALIZED (
          SELECT e."PageId" FROM "EffectivePageAccess" e
          WHERE e."UserId"=p_actor AND (p_space IS NULL OR e."SpaceId"=p_space)
        ), strict AS MATERIALIZED (
          SELECT count(*) AS hits FROM (
            SELECT 1 FROM "SearchDocument" d JOIN "Page" p ON p."Id"=d."PageId" CROSS JOIN actor a
            WHERE p_fuzzy AND NOT p."IsTrashed" AND p."PublishedRevisionId"=d."RevisionId"
              AND (p_space IS NULL OR d."SpaceId"=p_space)
              AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access))
              AND (d."Vector" @@ to_tsquery('simple',p_fts) OR d."AliasVector" @@ to_tsquery('simple',p_fts) OR (NOT p_phrase AND d."NormalizedTitle" LIKE p_prefix)
                OR p_query <% d."NormalizedTitle")
            LIMIT p_skip+p_take) found
        ), ranked AS MATERIALIZED (
          SELECT d."PageId",d."SpaceId",d."Title",d."PublishedAt",s."Name"::text AS "SpaceName",
            CASE WHEN d."NormalizedTitle"=p_query THEN 10000
                 WHEN d."NormalizedTitle" LIKE p_prefix THEN 5000
                 WHEN ts_filter(d."Vector",'{a}') @@ to_tsquery('simple',p_fts) THEN 3000
                 WHEN d."HeadingVector" @@ to_tsquery('simple',p_fts) THEN 2000
                 WHEN d."AliasVector" @@ to_tsquery('simple',p_fts) THEN 2500
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
              OR (p_fuzzy AND (p_query <% d."NormalizedTitle"
                OR ((SELECT hits FROM strict)<p_skip+p_take AND p_query <% d."NormalizedBody"))))
  ORDER BY score DESC,d."Title",d."PageId" LIMIT p_take OFFSET p_skip
        )
        SELECT r."PageId",r."SpaceId",r."SpaceName",r."Title",
          left(ts_headline('simple',d."Body",to_tsquery('simple',p_fts),
            'StartSel="", StopSel="", MaxWords=35, MinWords=15, MaxFragments=1'),2000),r."PublishedAt",
          h.heading->>'title',h.heading->>'headingId',matched.alias
        FROM ranked r JOIN "SearchDocument" d ON d."PageId"=r."PageId"
        LEFT JOIN LATERAL (
          SELECT heading FROM jsonb_array_elements(d."Headings") AS heading
          WHERE heading->>'headingId' IS NOT NULL
            AND (((p_phrase OR d."HeadingVector" @@ to_tsquery('simple',p_fts))
                AND atlas_search_lexemes(heading->>'title',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts))
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE (p_phrase OR d."AliasVector" @@ to_tsquery('simple',p_fts))
            AND atlas_search_lexemes(alias,atlas_normalize_search_text(alias)) @@ to_tsquery('simple',p_fts)
          ORDER BY alias LIMIT 1
        ) matched ON true
ORDER BY r.score DESC,r."Title",r."PageId"
$$;

CREATE OR REPLACE FUNCTION atlas_search_published_filtered(
  p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean,
  p_actor uuid, p_stamp text, p_space uuid, p_take integer, p_skip integer,
  p_changed_since timestamptz, p_latest boolean)
RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, snippet text,
              changed_at timestamptz, heading_title text, heading_id text, matched_alias text,
              score real)
LANGUAGE sql STABLE AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), access AS MATERIALIZED (
          SELECT e."PageId" FROM "EffectivePageAccess" e
          WHERE e."UserId"=p_actor AND (p_space IS NULL OR e."SpaceId"=p_space)
        ), strict AS MATERIALIZED (
          SELECT count(*) AS hits FROM (
            SELECT 1 FROM "SearchDocument" d JOIN "Page" p ON p."Id"=d."PageId" CROSS JOIN actor a
            WHERE p_fuzzy AND NOT p_latest AND NOT p."IsTrashed" AND p."PublishedRevisionId"=d."RevisionId"
              AND (p_space IS NULL OR d."SpaceId"=p_space)
              AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access))
              AND (d."Vector" @@ to_tsquery('simple',p_fts) OR d."AliasVector" @@ to_tsquery('simple',p_fts) OR (NOT p_phrase AND d."NormalizedTitle" LIKE p_prefix)
                OR p_query <% d."NormalizedTitle")
              AND (p_changed_since IS NULL OR d."PublishedAt">=p_changed_since)
            LIMIT p_skip+p_take) found
        ), ranked AS MATERIALIZED (
          SELECT d."PageId",d."SpaceId",d."Title",d."PublishedAt",s."Name"::text AS "SpaceName",
            CASE WHEN d."NormalizedTitle"=p_query THEN 10000
                 WHEN d."NormalizedTitle" LIKE p_prefix THEN 5000
                 WHEN ts_filter(d."Vector",'{a}') @@ to_tsquery('simple',p_fts) THEN 3000
                 WHEN d."HeadingVector" @@ to_tsquery('simple',p_fts) THEN 2000
                 WHEN d."AliasVector" @@ to_tsquery('simple',p_fts) THEN 2500
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
              OR (p_fuzzy AND (p_query <% d."NormalizedTitle"
                OR ((p_latest OR (SELECT hits FROM strict)<p_skip+p_take) AND p_query <% d."NormalizedBody"))))
    AND (p_changed_since IS NULL OR d."PublishedAt">=p_changed_since)
  ORDER BY CASE WHEN p_latest THEN d."PublishedAt" END DESC NULLS LAST,score DESC,d."Title",d."PageId" LIMIT p_take OFFSET p_skip
        )
        SELECT r."PageId",r."SpaceId",r."SpaceName",r."Title",
          left(ts_headline('simple',d."Body",to_tsquery('simple',p_fts),
            'StartSel="", StopSel="", MaxWords=35, MinWords=15, MaxFragments=1'),2000),r."PublishedAt",
          h.heading->>'title',h.heading->>'headingId',matched.alias,r.score::real
        FROM ranked r JOIN "SearchDocument" d ON d."PageId"=r."PageId"
        LEFT JOIN LATERAL (
          SELECT heading FROM jsonb_array_elements(d."Headings") AS heading
          WHERE heading->>'headingId' IS NOT NULL
            AND (((p_phrase OR d."HeadingVector" @@ to_tsquery('simple',p_fts))
                AND atlas_search_lexemes(heading->>'title',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts))
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE (p_phrase OR d."AliasVector" @@ to_tsquery('simple',p_fts))
            AND atlas_search_lexemes(alias,atlas_normalize_search_text(alias)) @@ to_tsquery('simple',p_fts)
          ORDER BY alias LIMIT 1
        ) matched ON true
ORDER BY CASE WHEN p_latest THEN r."PublishedAt" END DESC NULLS LAST,r.score DESC,r."Title",r."PageId"
$$;

CREATE OR REPLACE FUNCTION atlas_search_hybrid_candidates(
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
                 WHEN ts_filter(d."Vector",'{a}') @@ to_tsquery('simple',p_fts) THEN 3000
                   WHEN d."HeadingVector" @@ to_tsquery('simple',p_fts) THEN 2000
                   WHEN d."AliasVector" @@ to_tsquery('simple',p_fts) THEN 2500
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
            AND strpos(c."Text",E'\n')>0
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
