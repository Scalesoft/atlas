-- Search latency (UX cycle 3, SE3-1): GET /api/search took 300-600 ms over the 301 pages of the real wiki. Same rows, same
-- order; only work that cannot change the answer is skipped. Measured with EXPLAIN ANALYZE on a copy of that wiki:
-- 1. atlas_search_lexemes ran once per heading (and alias) of every hit, and the search transaction's plan_cache_mode
--    force_custom_plan made PostgreSQL 18 plan its SQL body again on every call: ~3 ms per hit, 160 ms per query. The function
--    keeps the default plan cache; the search statements themselves are still planned with their own arguments.
-- 2. A heading (or alias) can only match when the page's HeadingVector (AliasVector), the union of its headings' (aliases')
--    lexemes, matches; phrases skip that check, as positions past 16383 are clamped in the union.
-- 3. The fuzzy body test (p_query <% NormalizedBody, ~120 ns per character of every page) is skipped when the strict tests
--    already fill the page: a page found only by its body scores below 25 (0 + 50 * word similarity of a title under the
--    0.45 threshold), every strict hit 500 or more, so with skip+take strict hits none of them can reach the window. Ordered by
--    date (p_latest) it still runs.
-- 4. Drafts test the fuzzy body of real drafts only (the working revisions the scope can show), not of every working revision:
--    body_probe and its count decide between "all body hits" and the ordered path, both of which end with the same window.

ALTER FUNCTION atlas_search_lexemes(text,text) SET plan_cache_mode = 'auto';

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
          -- The body of each real draft, read through its key: a join would let the planner test every working revision.
          SELECT d."PageId" FROM accessible d
          WHERE p_fuzzy AND NOT (d."Vector" @@ to_tsquery('simple',p_fts)
            OR (NOT p_phrase AND d."NormalizedTitle" LIKE p_prefix))
            AND (SELECT p_query <% body."NormalizedBody" FROM "WorkingSearchDocument" body WHERE body."PageId"=d."PageId")
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
            AND (((p_phrase OR w."HeadingVector" @@ to_tsquery('simple',p_fts))
                AND atlas_search_lexemes(heading->>'title',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts))
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        ORDER BY CASE WHEN p_latest THEN r."ChangedAt" END DESC NULLS LAST,r.score DESC,r."Title",r."PageId"
$$;
