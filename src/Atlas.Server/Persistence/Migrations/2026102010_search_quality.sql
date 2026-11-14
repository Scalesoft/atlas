-- Search quality (2026-10-20, the owner's "Co je to freebusy" and "API" reports). New migration only; historical function
-- definitions remain immutable.
--
-- 1. OpenAPI components: the page's Markdown carries only their one-line summary (EditorComponentText.OpenApi), so paths,
--    operations, parameters and schemas were never searchable. The search body of a published or working revision now ends
--    with the components' sources (YAML or JSON; normalization makes their words lexemes); headings still come from the
--    page's own Markdown. The AI index renders the sources as sections in C# (OpenApiSearchText); the pages that hold one
--    are enqueued once below.
-- 2. A title-word tier (3000): every query word in the title (the A-weighted part of the vector) outranks headings and the
--    body. Until now only an exact title or a title starting with the query got a tier, so "API" listed pages mentioning
--    it before pages named "... API". The hybrid scope keeps every score >= 1500 as a tier above its fused order.
-- 3. A page without text has one chunk of its title alone; its vector won short queries and the hit carried no text
--    ("their content is empty"). Vector candidates skip such chunks, as atlas_answer_sources already does.

-- The searchable text of a revision's components; '' when it has none. Bounded like OpenApiSearchText.MaxCharacters.
CREATE FUNCTION atlas_search_component_text(content text) RETURNS text LANGUAGE sql IMMUTABLE STRICT AS $$
  SELECT CASE WHEN strpos(content,'"openapi"')=0 THEN '' ELSE COALESCE((
    SELECT left(string_agg(source #>> '{}', E'\n\n'), 300000)
    FROM jsonb_path_query(content::jsonb, 'strict $.** ? (@.type == "openapi").attrs.data.source', '{}', true) AS source
    WHERE jsonb_typeof(source)='string'), '') END
$$;

-- What search reads of a revision: its Markdown without password directives, then its components' text.
CREATE FUNCTION atlas_search_body(markdown text, content text) RETURNS text LANGUAGE sql IMMUTABLE STRICT AS $$
  SELECT CASE WHEN c.text='' THEN atlas_strip_secret_directives(markdown)
              ELSE atlas_strip_secret_directives(markdown) || E'\n\n' || c.text END
  FROM (SELECT atlas_search_component_text(content) AS text) c
$$;

CREATE OR REPLACE FUNCTION atlas_refresh_search_document() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW."PublishedRevisionId" IS NULL THEN
    DELETE FROM "SearchDocument" WHERE "PageId" = NEW."Id";
  ELSE
    INSERT INTO "SearchDocument"("PageId","SpaceId","RevisionId","Title","Body","NormalizedTitle","NormalizedBody","Vector")
    SELECT NEW."Id", NEW."SpaceId", r."Id", r."Title", b.body,
      atlas_normalize_search_text(r."Title"), atlas_normalize_search_text(b.body),
      setweight(to_tsvector('simple', atlas_normalize_search_text(r."Title")), 'A') ||
      setweight(to_tsvector('simple', atlas_normalize_search_text(b.body)), 'B')
    FROM "PageRevision" r CROSS JOIN LATERAL (SELECT atlas_search_body(r."Markdown",r."ContentJson") AS body) b
    WHERE r."Id" = NEW."PublishedRevisionId"
    ON CONFLICT ("PageId") DO UPDATE SET "SpaceId" = EXCLUDED."SpaceId", "RevisionId" = EXCLUDED."RevisionId",
      "Title" = EXCLUDED."Title", "Body" = EXCLUDED."Body", "NormalizedTitle" = EXCLUDED."NormalizedTitle",
      "NormalizedBody" = EXCLUDED."NormalizedBody", "Vector" = EXCLUDED."Vector";
  END IF;
  RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION atlas_project_working_search_document() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE source "PageRevision"%ROWTYPE; heading jsonb; body text; boundary tsvector := '''__scaleatlas_field_boundary__'':1'::tsvector;
BEGIN
  SELECT * INTO source FROM "PageRevision"
    WHERE "PageId"=NEW."PageId" AND "Id"=NEW."RevisionId" AND "ActiveTo" IS NULL;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Working search projection requires the current revision of its page.' USING ERRCODE='23503';
  END IF;
  body := atlas_search_body(source."Markdown",source."ContentJson");
  NEW."NormalizedTitle" := atlas_normalize_search_text(source."Title");
  NEW."NormalizedBody" := atlas_normalize_search_text(body);
  NEW."ChangedAt" := source."ActiveFrom";
  NEW."Headings" := atlas_search_headings(atlas_strip_secret_directives(source."Markdown"),source."ContentJson");
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
        ), ranked AS MATERIALIZED (
          SELECT d."PageId",d."SpaceId",d."Title",d."PublishedAt",s."Name"::text AS "SpaceName",
            CASE WHEN d."NormalizedTitle"=p_query THEN 10000
                 WHEN d."NormalizedTitle" LIKE p_prefix THEN 5000
                 WHEN ts_filter(d."Vector",'{a}') @@ to_tsquery('simple',p_fts) THEN 3000
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
            AND (to_tsvector('simple',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts)
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE to_tsvector('simple',atlas_normalize_search_text(alias)) @@ to_tsquery('simple',p_fts)
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
        ), ranked AS MATERIALIZED (
          SELECT d."PageId",d."SpaceId",d."Title",d."PublishedAt",s."Name"::text AS "SpaceName",
            CASE WHEN d."NormalizedTitle"=p_query THEN 10000
                 WHEN d."NormalizedTitle" LIKE p_prefix THEN 5000
                 WHEN ts_filter(d."Vector",'{a}') @@ to_tsquery('simple',p_fts) THEN 3000
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
            AND (to_tsvector('simple',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts)
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE to_tsvector('simple',atlas_normalize_search_text(alias)) @@ to_tsquery('simple',p_fts)
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

CREATE OR REPLACE FUNCTION atlas_search_drafts(
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
          WHERE e."UserId"=p_actor AND e."Role">=2 AND (p_space IS NULL OR e."SpaceId"=p_space)
        ), accessible AS MATERIALIZED (
          SELECT d."PageId",d."RevisionId",d."NormalizedTitle",d."Vector",d."HeadingVector",d."ChangedAt",
            p."SpaceId",s."Name"::text AS "SpaceName",r."Title"::text AS "Title"
          FROM "WorkingSearchDocument" d JOIN "Page" p ON p."Id"=d."PageId"
            JOIN "PageRevision" r ON r."PageId"=d."PageId" AND r."Id"=d."RevisionId" AND r."ActiveTo" IS NULL
            JOIN "Space" s ON s."Id"=p."SpaceId" CROSS JOIN actor a
          WHERE NOT p."IsTrashed" AND r."Id" IS DISTINCT FROM p."PublishedRevisionId"
            AND (p_space IS NULL OR p."SpaceId"=p_space)
            AND (p_changed_since IS NULL OR d."ChangedAt">=p_changed_since)
            AND (p."PublishedRevisionId" IS NOT NULL OR p."OwnerId"=p_actor OR a."IsWikiAdmin")
            AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access))
        ), body_probe AS MATERIALIZED (
          SELECT "PageId" FROM "WorkingSearchDocument"
          WHERE p_fuzzy AND NOT ("Vector" @@ to_tsquery('simple',p_fts)
            OR (NOT p_phrase AND "NormalizedTitle" LIKE p_prefix)) AND p_query <% "NormalizedBody"
          LIMIT 64
        ), matches AS MATERIALIZED (
          SELECT "PageId" FROM "WorkingSearchDocument" WHERE "Vector" @@ to_tsquery('simple',p_fts)
          UNION
          SELECT "PageId" FROM "WorkingSearchDocument" WHERE NOT p_phrase AND "NormalizedTitle" LIKE p_prefix
          UNION
          SELECT "PageId" FROM "WorkingSearchDocument"
          WHERE p_fuzzy AND NOT ("Vector" @@ to_tsquery('simple', p_fts)
            OR (NOT p_phrase AND "NormalizedTitle" LIKE p_prefix)) AND p_query <% "NormalizedTitle"
          UNION
          SELECT "PageId" FROM body_probe WHERE (SELECT count(*) FROM body_probe)<64
          UNION (
            SELECT ordered."PageId" FROM (
              SELECT d."PageId",d."ChangedAt",d."Title",500+50*word_similarity(p_query,d."NormalizedTitle") AS score
              FROM accessible d
              WHERE (SELECT count(*) FROM body_probe)=64 AND p_fuzzy
                AND NOT (d."Vector" @@ to_tsquery('simple',p_fts)
                  OR (NOT p_phrase AND d."NormalizedTitle" LIKE p_prefix))
              ORDER BY CASE WHEN p_latest THEN d."ChangedAt" END DESC NULLS LAST,score DESC,d."Title",d."PageId"
              OFFSET 0
            ) ordered
            WHERE (SELECT p_query <% body."NormalizedBody" FROM "WorkingSearchDocument" body WHERE body."PageId"=ordered."PageId")
            ORDER BY CASE WHEN p_latest THEN ordered."ChangedAt" END DESC NULLS LAST,ordered.score DESC,ordered."Title",ordered."PageId"
            LIMIT (p_skip+p_take)
          )
        ), ranked AS MATERIALIZED (
          SELECT d."PageId",d."RevisionId",d."SpaceId",d."SpaceName",d."Title",d."ChangedAt",
            CASE WHEN d."NormalizedTitle"=p_query THEN 10000 WHEN d."NormalizedTitle" LIKE p_prefix THEN 5000
                 WHEN ts_filter(d."Vector",'{a}') @@ to_tsquery('simple',p_fts) THEN 3000
                 WHEN d."HeadingVector" @@ to_tsquery('simple',p_fts) THEN 2000
                 WHEN d."Vector" @@ to_tsquery('simple',p_fts) THEN 1000 ELSE 500 END
              + 100 * ts_rank_cd(d."Vector",to_tsquery('simple',p_fts),32)
              + 50 * word_similarity(p_query,d."NormalizedTitle") AS score
          FROM matches match JOIN accessible d ON d."PageId"=match."PageId"
          ORDER BY CASE WHEN p_latest THEN d."ChangedAt" END DESC NULLS LAST,score DESC,d."Title",d."PageId"
          LIMIT p_take OFFSET p_skip
        )
        SELECT r."PageId",r."SpaceId",r."SpaceName",r."Title",
          left(ts_headline('simple',d."Markdown",to_tsquery('simple',p_fts),
            'StartSel="", StopSel="", MaxWords=35, MinWords=15, MaxFragments=1'),2000),r."ChangedAt",
          h.heading->>'title',h.heading->>'headingId',NULL::text,r.score::real
        FROM ranked r JOIN "PageRevision" d ON d."Id"=r."RevisionId"
          JOIN "WorkingSearchDocument" w ON w."PageId"=r."PageId" AND w."RevisionId"=r."RevisionId"
        LEFT JOIN LATERAL (
          SELECT heading FROM jsonb_array_elements(w."Headings") AS heading
          WHERE heading->>'headingId' IS NOT NULL
            AND (to_tsvector('simple',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts)
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        ORDER BY CASE WHEN p_latest THEN r."ChangedAt" END DESC NULLS LAST,r.score DESC,r."Title",r."PageId"
$$;

-- Existing rows: only revisions with a component change. The structure triggers recompute headings and vectors.
UPDATE "SearchDocument" d SET "Body"=atlas_search_body(r."Markdown",r."ContentJson"),
  "NormalizedBody"=atlas_normalize_search_text(atlas_search_body(r."Markdown",r."ContentJson"))
  FROM "PageRevision" r WHERE r."Id"=d."RevisionId" AND strpos(r."ContentJson",'"openapi"')>0;
UPDATE "WorkingSearchDocument" w SET "RevisionId"=w."RevisionId" FROM "PageRevision" r
  WHERE r."Id"=w."RevisionId" AND strpos(r."ContentJson",'"openapi"')>0;
-- The AI index chunks them again (nothing without a live generation).
SELECT atlas_enqueue_index_sync(p."Id") FROM "Page" p JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId"
  WHERE strpos(r."ContentJson",'"openapi"')>0;
