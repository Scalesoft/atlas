CREATE OR REPLACE FUNCTION atlas_space_in_ai(space uuid) RETURNS boolean
    LANGUAGE sql STABLE PARALLEL SAFE
    AS $$
    SELECT NOT EXISTS (
        SELECT 1 FROM "SpaceAiSetting" s
        WHERE s."SpaceId" = space AND s."ActiveTo" IS NULL AND NOT s."Included")
$$;

CREATE OR REPLACE FUNCTION atlas_normalize_search_text(value text) RETURNS text
    LANGUAGE sql STABLE STRICT
    AS $$
  SELECT trim(regexp_replace(
    regexp_replace(
      regexp_replace(
        regexp_replace(
          regexp_replace(lower(public.unaccent(value)), '\mc\+\+(?!\+)', 'cplusplus', 'g'),
          '\mc#(?!#)', 'csharp', 'g'),
        '\mcpp\M', 'cplusplus', 'g'),
      '\mc[[:space:]]+sharp\M', 'csharp', 'g'),
    '[^[:alnum:]]+', ' ', 'g'))
$$;

CREATE OR REPLACE FUNCTION atlas_strip_secret_directives(value text) RETURNS text
    LANGUAGE sql IMMUTABLE STRICT
    AS $$
  SELECT regexp_replace(value, '::?secret\{[^{}' || chr(10) || ']*\}', ' ', 'g')
$$;

CREATE OR REPLACE FUNCTION atlas_search_stem(word text) RETURNS text
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
    AS $_$
  SELECT COALESCE(substring(word from '^(.{4,}?)(?:ami|ach|ich|ymi|ech|um|ou|em|im|am|a|u|y|e|i|o)$'), word)
$_$;

CREATE OR REPLACE FUNCTION atlas_search_identifiers(raw text) RETURNS text
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    AS $$
  SELECT string_agg(array_to_string(parts,' ') || ' ' ||
    (SELECT string_agg(array_to_string(parts[i:],''),' ' ORDER BY i) FROM generate_series(1,cardinality(parts)-1) i),' ' ORDER BY parts)
  FROM (
    SELECT DISTINCT parts[greatest(1,cardinality(parts)-7):] AS parts FROM (
      SELECT string_to_array(atlas_normalize_search_text(regexp_replace(regexp_replace(token,
        '([A-Z]+)([A-Z][a-z])','\1 \2','g'),'([a-z0-9])([A-Z])','\1 \2','g')),' ') AS parts
      FROM (SELECT btrim(token,'_.-') AS token FROM regexp_split_to_table(public.unaccent(raw),'[^[:alnum:]_.-]+') AS token) t
      WHERE token ~ '^[[:alpha:]]' AND token ~ '[[:alnum:]][_.-]+[[:alnum:]]|[a-z0-9][A-Z]|[A-Z][A-Z][a-z]') split
    WHERE cardinality(parts)>1) identifier
$$;

CREATE OR REPLACE FUNCTION atlas_search_lexemes(raw text, normalized text) RETURNS tsvector
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET plan_cache_mode TO 'auto'
    AS $$
  SELECT COALESCE((
      SELECT string_agg(quote_literal(word) || ':' || positions, ' ')::tsvector FROM (
        SELECT word, string_agg(position::text, ',' ORDER BY position) AS positions FROM (
          SELECT e.lexeme AS word, e.positions FROM unnest(exact.vector) e
          UNION ALL
          SELECT atlas_search_stem(e.lexeme), e.positions FROM unnest(exact.vector) e WHERE atlas_search_stem(e.lexeme)<>e.lexeme
        ) words CROSS JOIN LATERAL unnest(words.positions) AS position GROUP BY word) grouped), ''::tsvector)
    || to_tsvector('simple',COALESCE(atlas_search_identifiers(raw),''))
  FROM (SELECT to_tsvector('simple',normalized) AS vector) exact
$$;

CREATE OR REPLACE FUNCTION atlas_search_component_text(content text) RETURNS text
    LANGUAGE sql IMMUTABLE STRICT
    AS $_$
  SELECT CASE WHEN strpos(content,'"openapi"')=0 THEN '' ELSE COALESCE((
    SELECT left(string_agg(source #>> '{}', E'\n\n'), 300000)
    FROM jsonb_path_query(content::jsonb, 'strict $.** ? (@.type == "openapi").attrs.data.source', '{}', true) AS source
    WHERE jsonb_typeof(source)='string'), '') END
$_$;

CREATE OR REPLACE FUNCTION atlas_search_body(markdown text, content text) RETURNS text
    LANGUAGE sql IMMUTABLE STRICT
    AS $$
  SELECT CASE WHEN c.text='' THEN atlas_strip_secret_directives(markdown)
              ELSE atlas_strip_secret_directives(markdown) || E'\n\n' || c.text END
  FROM (SELECT atlas_search_component_text(content) AS text) c
$$;

CREATE OR REPLACE FUNCTION atlas_search_headings(markdown text, rich text) RETURNS jsonb
    LANGUAGE plpgsql STABLE
    AS $_$
DECLARE line text; heading text; fence text; marker text; result jsonb := '[]'; node jsonb;
        normalized text; rich_title text; anchor text; anchors jsonb := '{}';
BEGIN
  -- Traverse canonical rich nodes once per publication, not once per heading or query.
  FOR node IN SELECT value FROM jsonb_path_query(rich::jsonb->'document',
    'strict $.** ? (@.type == "heading")', '{}'::jsonb, true) AS value LOOP
    SELECT string_agg(value #>> '{}','') INTO rich_title
    FROM jsonb_path_query(node,'strict $.** ? (exists(@.text)).text','{}'::jsonb,true) AS value;
    normalized := atlas_normalize_search_text(COALESCE(rich_title,''));
    anchor := CASE WHEN node->'attrs'->>'headingId' ~ '^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$'
      THEN node->'attrs'->>'headingId' ELSE NULL END;
    anchors := jsonb_set(anchors,ARRAY[normalized],COALESCE(anchors->normalized,'[]'::jsonb)||jsonb_build_array(anchor));
  END LOOP;
  FOREACH line IN ARRAY string_to_array(markdown,E'\n') LOOP
    marker := substring(line from '^ {0,3}(`{3,}|~{3,})');
    IF fence IS NOT NULL THEN
      IF marker IS NOT NULL AND left(marker,1)=left(fence,1) AND length(marker)>=length(fence)
        AND line ~ '^ {0,3}(`{3,}|~{3,})[[:space:]]*$' THEN fence := NULL; END IF;
      CONTINUE;
    END IF;
    IF marker IS NOT NULL THEN fence := marker; CONTINUE; END IF;
    heading := substring(line from '^ {0,3}#{1,6}[[:blank:]]+(.+)$');
    IF heading IS NULL THEN CONTINUE; END IF;
    heading := trim(regexp_replace(heading,'[[:blank:]]+#+[[:blank:]]*$',''));
    heading := regexp_replace(heading,'!?\[([^]]+)\]\([^)]*\)','\1','g');
    heading := regexp_replace(heading,'[*_`]+','','g');
    normalized := atlas_normalize_search_text(heading);
    anchor := NULL;
    -- Duplicate titles consume their own persistent anchor in document order.
    anchor := anchors->normalized->>0;
    IF anchors ? normalized THEN anchors := jsonb_set(anchors,ARRAY[normalized],(anchors->normalized)-0); END IF;
    result := result || jsonb_build_array(jsonb_build_object('title',heading,'normalizedTitle',normalized,'headingId',anchor));
  END LOOP;
  RETURN result;
END $_$;

CREATE OR REPLACE FUNCTION atlas_comment_search_text(body text) RETURNS text
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
    AS $$
    SELECT regexp_replace(body, '\]\([^)]*\)', ']', 'g');
$$;

CREATE OR REPLACE FUNCTION atlas_search_published(p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean, p_actor uuid, p_stamp text, p_space uuid, p_take integer, p_skip integer) RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, snippet text, changed_at timestamp with time zone, heading_title text, heading_id text, matched_alias text)
    LANGUAGE sql STABLE
    AS $$
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
        ), body AS MATERIALIZED (
          SELECT d."PageId" FROM "SearchDocument" d
          WHERE p_fuzzy AND (SELECT hits FROM strict)<p_skip+p_take AND p_query <% left(d."NormalizedBody",20000)
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
                OR d."PageId" IN (SELECT "PageId" FROM body))))
  ORDER BY score DESC,d."Title",d."PageId" LIMIT p_take OFFSET p_skip
        )
        SELECT r."PageId",r."SpaceId",r."SpaceName",r."Title",
          left(ts_headline('simple',substr(d."Body",greatest(strpos(d."NormalizedBody",split_part(p_query,' ',1))-10000,1),20000),
            to_tsquery('simple',p_fts),
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

CREATE OR REPLACE FUNCTION atlas_search_published_filtered(p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean, p_actor uuid, p_stamp text, p_space uuid, p_take integer, p_skip integer, p_changed_since timestamp with time zone, p_latest boolean) RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, snippet text, changed_at timestamp with time zone, heading_title text, heading_id text, matched_alias text, score real)
    LANGUAGE sql STABLE
    AS $$
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
        ), body AS MATERIALIZED (
          SELECT d."PageId" FROM "SearchDocument" d
          WHERE p_fuzzy AND (p_latest OR (SELECT hits FROM strict)<p_skip+p_take) AND p_query <% left(d."NormalizedBody",20000)
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
                OR d."PageId" IN (SELECT "PageId" FROM body))))
    AND (p_changed_since IS NULL OR d."PublishedAt">=p_changed_since)
  ORDER BY CASE WHEN p_latest THEN d."PublishedAt" END DESC NULLS LAST,score DESC,d."Title",d."PageId" LIMIT p_take OFFSET p_skip
        )
        SELECT r."PageId",r."SpaceId",r."SpaceName",r."Title",
          left(ts_headline('simple',substr(d."Body",greatest(strpos(d."NormalizedBody",split_part(p_query,' ',1))-10000,1),20000),
            to_tsquery('simple',p_fts),
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

CREATE OR REPLACE FUNCTION atlas_search_drafts(p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean, p_actor uuid, p_stamp text, p_space uuid, p_take integer, p_skip integer, p_changed_since timestamp with time zone, p_latest boolean) RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, snippet text, changed_at timestamp with time zone, heading_title text, heading_id text, matched_alias text, score real)
    LANGUAGE sql STABLE
    AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), access AS MATERIALIZED (
          SELECT e."PageId",e."Role" FROM "EffectivePageAccess" e
          WHERE e."UserId"=p_actor AND e."Role">=1 AND (p_space IS NULL OR e."SpaceId"=p_space)
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
            AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access
              WHERE "Role">=2 OR coalesce(p."PublicationMode",s."PublicationMode")='Live'))
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

CREATE OR REPLACE FUNCTION atlas_search_all(p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean, p_actor uuid, p_stamp text, p_space uuid, p_take integer, p_skip integer, p_changed_since timestamp with time zone, p_latest boolean) RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, snippet text, changed_at timestamp with time zone, heading_title text, heading_id text, matched_alias text, is_draft boolean)
    LANGUAGE sql STABLE
    AS $$
  WITH published AS MATERIALIZED (
    SELECT * FROM atlas_search_published_filtered(p_query,p_prefix,p_fts,p_phrase,p_fuzzy,
      p_actor,p_stamp,p_space,p_skip+p_take,0,p_changed_since,p_latest)
  ), draft AS MATERIALIZED (
    SELECT * FROM atlas_search_drafts(p_query,p_prefix,p_fts,p_phrase,p_fuzzy,
      p_actor,p_stamp,p_space,p_skip+p_take,0,p_changed_since,p_latest)
  ), merged AS (
    SELECT p.*,false AS is_draft FROM published p
    UNION ALL
    SELECT d.*,true AS is_draft FROM draft d
    WHERE NOT EXISTS (SELECT 1 FROM published p WHERE p.page_id=d.page_id)
  )
  SELECT page_id,space_id,space_name,title,snippet,changed_at,heading_title,heading_id,matched_alias,is_draft
  FROM merged
  ORDER BY CASE WHEN p_latest THEN changed_at END DESC NULLS LAST,score DESC,title,page_id
  LIMIT p_take OFFSET p_skip
$$;

CREATE OR REPLACE FUNCTION atlas_search_spaces(p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean, p_actor uuid, p_stamp text, p_space uuid) RETURNS TABLE(space_id uuid, space_name text, space_icon text, space_color text)
    LANGUAGE sql STABLE
    AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), visible AS MATERIALIZED (
          SELECT s.*,atlas_normalize_search_text(s."Name") AS normalized FROM "Space" s CROSS JOIN actor a
          WHERE s."TrashedAt" IS NULL AND (p_space IS NULL OR s."Id"=p_space) AND (a."IsWikiAdmin" OR EXISTS (
            SELECT 1 FROM "EffectiveSpaceGrant" g WHERE g."SpaceId"=s."Id"
              AND (g."UserId"=p_actor OR EXISTS (
                SELECT 1 FROM "EffectiveGroupMember" m WHERE m."GroupId"=g."GroupId" AND m."UserId"=p_actor AND m."ActiveTo" IS NULL)))
            -- D8: a space in which the account can list a page shows its name, icon and colour.
            OR EXISTS (
              SELECT 1 FROM "EffectivePageAccess" e JOIN "Page" p ON p."Id"=e."PageId"
              WHERE e."UserId"=p_actor AND e."SpaceId"=s."Id" AND NOT p."IsTrashed"
                AND (p."PublishedRevisionId" IS NOT NULL OR (e."Role">=2 AND p."OwnerId"=p_actor))))
        )
        SELECT "Id","Name"::text,"Icon"::text,"Color"::text FROM visible
        WHERE (NOT p_phrase AND normalized LIKE p_prefix) OR to_tsvector('simple',normalized) @@ to_tsquery('simple',p_fts)
          OR (p_fuzzy AND p_query <% normalized)
        ORDER BY CASE WHEN normalized=p_query THEN 2 WHEN normalized LIKE p_prefix THEN 1 ELSE 0 END DESC,
          word_similarity(p_query,normalized) DESC,"Name","Id" LIMIT 10
$$;

CREATE OR REPLACE FUNCTION atlas_search_did_you_mean(p_query text, p_actor uuid, p_stamp text, p_space uuid) RETURNS text
    LANGUAGE sql STABLE
    AS $$
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

CREATE OR REPLACE FUNCTION atlas_search_hybrid_candidates(p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean, p_actor uuid, p_stamp text, p_space uuid, p_lexical_take integer, p_changed_since timestamp with time zone, p_latest boolean, p_generation uuid, p_embedding public.vector, p_min_similarity real) RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, changed_at timestamp with time zone, lexical_score real, lexical_rank integer, vector_rank integer, chunk_id bigint, chunk_rank integer, semantic boolean)
    LANGUAGE sql STABLE
    AS $$
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
            LIMIT p_lexical_take) found
        ), body AS MATERIALIZED (
          SELECT d."PageId" FROM "SearchDocument" d
          WHERE p_fuzzy AND (p_latest OR (SELECT hits FROM strict)<p_lexical_take) AND p_query <% left(d."NormalizedBody",20000)
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
                OR (p_fuzzy AND (p_query <% d."NormalizedTitle"
                  OR d."PageId" IN (SELECT "PageId" FROM body))))
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

CREATE OR REPLACE FUNCTION atlas_search_hybrid(p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean, p_actor uuid, p_stamp text, p_space uuid, p_take integer, p_skip integer, p_changed_since timestamp with time zone, p_latest boolean, p_generation uuid, p_embedding public.vector, p_min_similarity real, p_drafts boolean) RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, snippet text, changed_at timestamp with time zone, heading_title text, heading_id text, matched_alias text, score real, is_draft boolean, semantic boolean, passage text, passage_heading_title text, passage_heading_id text)
    LANGUAGE sql STABLE
    AS $$
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
            AND (atlas_search_lexemes(heading->>'title',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts)
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE NOT r.is_draft AND NOT r.semantic
            AND atlas_search_lexemes(alias,atlas_normalize_search_text(alias)) @@ to_tsquery('simple',p_fts)
          ORDER BY alias LIMIT 1
        ) matched ON true
        ORDER BY r.position
$$;

CREATE OR REPLACE FUNCTION atlas_search_hybrid_all(p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean, p_actor uuid, p_stamp text, p_space uuid, p_take integer, p_skip integer, p_changed_since timestamp with time zone, p_latest boolean, p_generation uuid, p_embedding public.vector, p_min_similarity real DEFAULT 0.45) RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, snippet text, changed_at timestamp with time zone, heading_title text, heading_id text, matched_alias text, is_draft boolean, semantic boolean, passage text, passage_heading_title text, passage_heading_id text)
    LANGUAGE sql STABLE
    AS $$
  SELECT page_id,space_id,space_name,title,snippet,changed_at,heading_title,heading_id,matched_alias,
    is_draft,semantic,passage,passage_heading_title,passage_heading_id
  FROM atlas_search_hybrid(p_query,p_prefix,p_fts,p_phrase,p_fuzzy,p_actor,p_stamp,p_space,p_take,p_skip,
    p_changed_since,p_latest,p_generation,p_embedding,p_min_similarity,true)
$$;

CREATE OR REPLACE FUNCTION atlas_search_hybrid_published(p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean, p_actor uuid, p_stamp text, p_space uuid, p_take integer, p_skip integer, p_changed_since timestamp with time zone, p_latest boolean, p_generation uuid, p_embedding public.vector, p_min_similarity real DEFAULT 0.45) RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, snippet text, changed_at timestamp with time zone, heading_title text, heading_id text, matched_alias text, score real, semantic boolean, passage text, passage_heading_title text, passage_heading_id text)
    LANGUAGE sql STABLE
    AS $$
  SELECT page_id,space_id,space_name,title,snippet,changed_at,heading_title,heading_id,matched_alias,score,
    semantic,passage,passage_heading_title,passage_heading_id
  FROM atlas_search_hybrid(p_query,p_prefix,p_fts,p_phrase,p_fuzzy,p_actor,p_stamp,p_space,p_take,p_skip,
    p_changed_since,p_latest,p_generation,p_embedding,p_min_similarity,false)
$$;

CREATE OR REPLACE FUNCTION atlas_similar_pages(p_query text, p_words text, p_actor uuid, p_stamp text, p_space uuid, p_exclude uuid, p_take integer) RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, is_draft boolean, matched_by text, heading_title text, heading_id text, score real)
    LANGUAGE sql STABLE
    AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), access AS MATERIALIZED (
          SELECT e."PageId",e."Role" FROM "EffectivePageAccess" e WHERE e."UserId"=p_actor AND e."Role">=1
        ), candidates AS MATERIALIZED (
          SELECT d."PageId",d."SpaceId",d."Title",false AS "IsDraft",d."NormalizedTitle",d."Aliases",d."Headings"
          FROM "SearchDocument" d JOIN "Page" p ON p."Id"=d."PageId" CROSS JOIN actor a
          WHERE NOT p."IsTrashed" AND NOT p."IsTemplate" AND p."PublishedRevisionId"=d."RevisionId"
            AND d."PageId" IS DISTINCT FROM p_exclude
            AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access))
            -- to_tsquery is strict: NULLIF keeps an empty word list from reaching it (no notice, no match).
            AND (d."NormalizedTitle" % p_query
              OR d."AliasVector" @@ to_tsquery('simple',NULLIF(p_words,''))
              OR (d."Vector" @@ to_tsquery('simple',NULLIF(p_words,''))
                AND d."HeadingVector" @@ to_tsquery('simple',NULLIF(p_words,''))))
          UNION ALL
          SELECT d."PageId",p."SpaceId",r."Title"::text,true,d."NormalizedTitle",'[]'::jsonb,d."Headings"
          FROM "WorkingSearchDocument" d JOIN "Page" p ON p."Id"=d."PageId"
            JOIN "PageRevision" r ON r."PageId"=d."PageId" AND r."Id"=d."RevisionId" AND r."ActiveTo" IS NULL
            CROSS JOIN actor a
          WHERE p."PublishedRevisionId" IS NULL AND p."OwnerId"=p_actor AND NOT p."IsTrashed" AND NOT p."IsTemplate"
            AND d."PageId" IS DISTINCT FROM p_exclude
            AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access WHERE "Role">=2))
            AND (d."NormalizedTitle" % p_query
              OR (d."Vector" @@ to_tsquery('simple',NULLIF(p_words,''))
                AND d."HeadingVector" @@ to_tsquery('simple',NULLIF(p_words,''))))
        ), scored AS (
          SELECT c."PageId",c."SpaceId",c."Title",c."IsDraft",
            similarity(c."NormalizedTitle",p_query) AS title_score,
            COALESCE((SELECT max(similarity(atlas_normalize_search_text(alias),p_query))
              FROM jsonb_array_elements_text(c."Aliases") AS alias),0) AS alias_score,
            h.heading_title,h.heading_id,COALESCE(h.heading_score,0) AS heading_score
          FROM candidates c
          LEFT JOIN LATERAL (
            SELECT heading->>'title' AS heading_title,heading->>'headingId' AS heading_id,
              CASE WHEN similarity(heading->>'normalizedTitle',p_query)>=0.5
                THEN similarity(heading->>'normalizedTitle',p_query) END AS heading_score
            FROM jsonb_array_elements(c."Headings") WITH ORDINALITY AS section(heading,position)
            WHERE heading->>'headingId' IS NOT NULL AND length(heading->>'normalizedTitle')>=6
              AND word_similarity(heading->>'normalizedTitle',p_query)>=0.8
            ORDER BY similarity(heading->>'normalizedTitle',p_query) DESC,
              word_similarity(heading->>'normalizedTitle',p_query) DESC,position LIMIT 1
          ) h ON true
        )
        SELECT r."PageId",r."SpaceId",s."Name"::text,r."Title",r."IsDraft",
          CASE WHEN r.title_score>=r.alias_score AND r.title_score>=r.heading_score THEN 'title'
               WHEN r.alias_score>=r.heading_score THEN 'alias' ELSE 'heading' END,
          r.heading_title,r.heading_id,greatest(r.title_score,r.alias_score,r.heading_score)::real AS score
        FROM scored r JOIN "Space" s ON s."Id"=r."SpaceId"
        WHERE greatest(r.title_score,r.alias_score,r.heading_score)>=0.35
        ORDER BY score DESC,CASE WHEN r."SpaceId"=p_space THEN 0 ELSE 1 END,r."Title",r."PageId"
        LIMIT p_take
$$;

CREATE OR REPLACE FUNCTION atlas_similar_pages_semantic(p_generation uuid, p_vector public.vector, p_actor uuid, p_stamp text, p_space uuid, p_exclude uuid, p_take integer, p_min_similarity real DEFAULT 0.75) RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, score real)
    LANGUAGE sql STABLE
    AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), access AS MATERIALIZED (
          SELECT e."PageId" FROM "EffectivePageAccess" e WHERE e."UserId"=p_actor AND e."Role">=1
        ), generation AS MATERIALIZED (
          SELECT g."Id" FROM "IndexGeneration" g WHERE g."Id"=p_generation AND g."State"='Active'
        ), included AS MATERIALIZED (
          SELECT s."Id" FROM "Space" s WHERE atlas_space_in_ai(s."Id")
        ), by_title AS MATERIALIZED (
          -- Tunable: 40 candidates from each index. Correlated filters on the scanned row, as in hybrid search.
          SELECT k."PageId",k."TitleEmbedding" <=> p_vector AS distance
          FROM "KnowledgePage" k
          WHERE k."GenerationId"=p_generation AND NOT k."IsTemplate" AND EXISTS (SELECT 1 FROM generation) AND EXISTS (SELECT 1 FROM actor)
            AND k."PageId" IS DISTINCT FROM p_exclude
            AND (SELECT p."PublishedRevisionId"=k."RevisionId" AND NOT p."IsTrashed" AND NOT p."IsTemplate"
                   AND p."SpaceId" = ANY (ARRAY(SELECT "Id" FROM included))
                 FROM "Page" p WHERE p."Id"=k."PageId")
            AND ((SELECT a."IsWikiAdmin" FROM actor a) OR k."PageId" IN (SELECT "PageId" FROM access))
          ORDER BY k."TitleEmbedding" <=> p_vector
          LIMIT 40
        ), by_content AS MATERIALIZED (
          SELECT k."PageId",k."Centroid" <=> p_vector AS distance
          FROM "KnowledgePage" k
          WHERE k."GenerationId"=p_generation AND k."Centroid" IS NOT NULL AND NOT k."IsTemplate"
            AND EXISTS (SELECT 1 FROM generation) AND EXISTS (SELECT 1 FROM actor)
            AND k."PageId" IS DISTINCT FROM p_exclude
            AND (SELECT p."PublishedRevisionId"=k."RevisionId" AND NOT p."IsTrashed" AND NOT p."IsTemplate"
                   AND p."SpaceId" = ANY (ARRAY(SELECT "Id" FROM included))
                 FROM "Page" p WHERE p."Id"=k."PageId")
            AND ((SELECT a."IsWikiAdmin" FROM actor a) OR k."PageId" IN (SELECT "PageId" FROM access))
          ORDER BY k."Centroid" <=> p_vector
          LIMIT 40
        ), scored AS (
          SELECT x."PageId",max(1-x.distance) AS score
          FROM (SELECT * FROM by_title UNION ALL SELECT * FROM by_content) x
          GROUP BY x."PageId"
        )
        SELECT r."PageId",p."SpaceId",s."Name"::text,d."Title"::text,r.score::real
        FROM scored r JOIN "Page" p ON p."Id"=r."PageId"
          JOIN "SearchDocument" d ON d."PageId"=p."Id" AND d."RevisionId"=p."PublishedRevisionId"
          JOIN "Space" s ON s."Id"=p."SpaceId"
        WHERE r.score>=p_min_similarity
        ORDER BY r.score DESC,CASE WHEN p."SpaceId"=p_space THEN 0 ELSE 1 END,d."Title",r."PageId"
        LIMIT p_take
$$;

CREATE OR REPLACE FUNCTION atlas_link_candidates(p_phrases text[], p_actor uuid, p_stamp text, p_exclude uuid, p_space uuid) RETURNS TABLE(phrase text, page_id uuid, space_id uuid, space_name text, title text, matched_by text)
    LANGUAGE sql STABLE
    AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), wanted AS MATERIALIZED (
          SELECT DISTINCT w AS phrase FROM unnest(p_phrases) AS w WHERE length(w) > 0
        ), matched AS MATERIALIZED (
          SELECT w.phrase,d."PageId",d."RevisionId",d."Title"::text AS "Title",'title' AS matched_by
          FROM wanted w JOIN "SearchDocument" d ON d."NormalizedTitle"=w.phrase
          UNION ALL
          SELECT w.phrase,d."PageId",d."RevisionId",d."Title"::text,'alias'
          FROM "SearchDocument" d CROSS JOIN LATERAL jsonb_array_elements_text(d."Aliases") AS alias
            JOIN wanted w ON w.phrase=atlas_normalize_search_text(alias)
          WHERE d."Aliases"<>'[]'::jsonb
        ), readable AS MATERIALIZED (
          SELECT DISTINCT ON (m.phrase,m."PageId") m.phrase,m."PageId",p."SpaceId",m."Title",m.matched_by
          FROM matched m JOIN "Page" p ON p."Id"=m."PageId" AND p."PublishedRevisionId"=m."RevisionId" CROSS JOIN actor a
          WHERE NOT p."IsTrashed" AND NOT p."IsTemplate" AND m."PageId" IS DISTINCT FROM p_exclude
            AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
              WHERE e."UserId"=p_actor AND e."PageId"=m."PageId" AND e."Role">=1))
          ORDER BY m.phrase,m."PageId",CASE WHEN m.matched_by='title' THEN 0 ELSE 1 END
        ), ranked AS (
          SELECT r.*,row_number() OVER (PARTITION BY r.phrase ORDER BY CASE WHEN r.matched_by='title' THEN 0 ELSE 1 END,
            CASE WHEN r."SpaceId"=p_space THEN 0 ELSE 1 END,r."Title",r."PageId") AS position
          FROM readable r
        )
        SELECT r.phrase,r."PageId",r."SpaceId",s."Name"::text,r."Title",r.matched_by
        FROM ranked r JOIN "Space" s ON s."Id"=r."SpaceId"
        WHERE r.position<=3
        ORDER BY r.phrase,r.position
$$;

CREATE OR REPLACE FUNCTION atlas_link_candidates_semantic(p_generation uuid, p_phrases text[], p_vectors public.vector[], p_actor uuid, p_stamp text, p_exclude uuid, p_min_similarity real DEFAULT 0.80) RETURNS TABLE(phrase text, page_id uuid, space_id uuid, space_name text, title text, similarity real)
    LANGUAGE sql STABLE
    AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), access AS MATERIALIZED (
          SELECT e."PageId" FROM "EffectivePageAccess" e WHERE e."UserId"=p_actor AND e."Role">=1
        ), generation AS MATERIALIZED (
          SELECT g."Id" FROM "IndexGeneration" g WHERE g."Id"=p_generation AND g."State"='Active'
        ), included AS MATERIALIZED (
          SELECT s."Id" FROM "Space" s WHERE atlas_space_in_ai(s."Id")
        ), wanted AS MATERIALIZED (
          SELECT w.phrase,w.vector FROM unnest(p_phrases,p_vectors) AS w(phrase,vector) WHERE w.vector IS NOT NULL
        ), nearest AS MATERIALIZED (
          SELECT w.phrase,n."PageId",n.distance FROM wanted w CROSS JOIN LATERAL (
            -- Tunable: 5 candidates per phrase; relaxed order is re-ranked below.
            SELECT k."PageId",k."TitleEmbedding" <=> w.vector AS distance
            FROM "KnowledgePage" k
            WHERE k."GenerationId"=p_generation AND NOT k."IsTemplate" AND EXISTS (SELECT 1 FROM generation) AND EXISTS (SELECT 1 FROM actor)
              AND k."PageId" IS DISTINCT FROM p_exclude
              AND (SELECT p."PublishedRevisionId"=k."RevisionId" AND NOT p."IsTrashed" AND NOT p."IsTemplate"
                     AND p."SpaceId" = ANY (ARRAY(SELECT "Id" FROM included))
                   FROM "Page" p WHERE p."Id"=k."PageId")
              AND ((SELECT a."IsWikiAdmin" FROM actor a) OR k."PageId" IN (SELECT "PageId" FROM access))
            ORDER BY k."TitleEmbedding" <=> w.vector
            LIMIT 5
          ) n
        ), best AS (
          SELECT DISTINCT ON (n.phrase) n.phrase,n."PageId",1-n.distance AS similarity
          FROM nearest n WHERE 1-n.distance>=p_min_similarity
          ORDER BY n.phrase,n.distance,n."PageId"
        )
        SELECT b.phrase,b."PageId",p."SpaceId",s."Name"::text,d."Title"::text,b.similarity::real
        FROM best b JOIN "Page" p ON p."Id"=b."PageId"
          JOIN "SearchDocument" d ON d."PageId"=p."Id" AND d."RevisionId"=p."PublishedRevisionId"
          JOIN "Space" s ON s."Id"=p."SpaceId"
        ORDER BY b.phrase
$$;

CREATE OR REPLACE FUNCTION atlas_rank_by_context(p_generation uuid, p_vector public.vector, p_candidates uuid[], p_actor uuid, p_stamp text) RETURNS TABLE(page_id uuid, similarity real)
    LANGUAGE sql STABLE
    AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), generation AS MATERIALIZED (
          SELECT g."Id" FROM "IndexGeneration" g WHERE g."Id"=p_generation AND g."State"='Active'
        ), candidate AS MATERIALIZED (
          SELECT p."Id",p."SpaceId",COALESCE(k."Centroid",k."TitleEmbedding") AS embedding
          FROM "Page" p
          JOIN "KnowledgePage" k ON k."GenerationId"=p_generation AND k."PageId"=p."Id" AND k."RevisionId"=p."PublishedRevisionId"
          CROSS JOIN actor a
          WHERE p."Id"=ANY(p_candidates) AND EXISTS (SELECT 1 FROM generation) AND NOT p."IsTrashed" AND NOT p."IsTemplate"
            AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
              WHERE e."UserId"=p_actor AND e."PageId"=p."Id" AND e."Role">=1))
        ), included AS MATERIALIZED (
          SELECT x."SpaceId" FROM (SELECT DISTINCT "SpaceId" FROM candidate) x WHERE atlas_space_in_ai(x."SpaceId")
        )
        SELECT c."Id",(1-(c.embedding <=> p_vector))::real AS similarity
        FROM candidate c JOIN included i ON i."SpaceId"=c."SpaceId"
        ORDER BY similarity DESC,c."Id"
$$;

CREATE OR REPLACE FUNCTION atlas_placement_parent(p_generation uuid, p_vector public.vector, p_space uuid, p_actor uuid, p_stamp text, p_root_allowed boolean, p_min_similarity real DEFAULT 0.45) RETURNS TABLE(parent_id uuid, path_ids uuid[], path_titles text[])
    LANGUAGE sql STABLE
    AS $$
        WITH RECURSIVE actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), generation AS MATERIALIZED (
          SELECT g."Id" FROM "IndexGeneration" g WHERE g."Id"=p_generation AND g."State"='Active'
        ), nearest AS MATERIALIZED (
          SELECT p."Id",p."ParentId",
            greatest(1-(k."TitleEmbedding" <=> p_vector),COALESCE(1-(k."Centroid" <=> p_vector),0)) AS similarity
          FROM "Page" p
          JOIN "KnowledgePage" k ON k."GenerationId"=p_generation AND k."PageId"=p."Id" AND k."RevisionId"=p."PublishedRevisionId"
          CROSS JOIN actor a
          WHERE p."SpaceId"=p_space AND NOT p."IsTrashed" AND NOT p."IsTemplate"
            AND EXISTS (SELECT 1 FROM generation) AND (SELECT atlas_space_in_ai(p_space))
            AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
              WHERE e."UserId"=p_actor AND e."PageId"=p."Id" AND e."Role">=1))
        ), top AS MATERIALIZED (
          SELECT n."Id",n."ParentId",(row_number() OVER (ORDER BY n.similarity DESC,n."Id"))::integer AS position
          FROM nearest n WHERE n.similarity>=p_min_similarity
          ORDER BY n.similarity DESC,n."Id"
          LIMIT 5
        ), candidate AS MATERIALIZED (
          SELECT t."ParentId" AS parent_id,count(*) AS votes,min(t.position) AS best FROM top t GROUP BY t."ParentId"
        ), chain(candidate,id,parent,depth) AS (
          SELECT c.parent_id,p."Id",p."ParentId",0 FROM candidate c JOIN "Page" p ON p."Id"=c.parent_id
          UNION ALL
          SELECT ch.candidate,p."Id",p."ParentId",ch.depth+1 FROM chain ch JOIN "Page" p ON p."Id"=ch.parent WHERE ch.depth<64
        ), step AS MATERIALIZED (
          SELECT ch.candidate,ch.depth,ch.id,d."Title"::text AS title,
            (d."PageId" IS NOT NULL AND NOT p."IsTrashed" AND NOT p."IsTemplate" AND p."SpaceId"=p_space
              AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
                WHERE e."UserId"=p_actor AND e."PageId"=p."Id" AND e."Role">=1))) AS readable
          FROM chain ch JOIN "Page" p ON p."Id"=ch.id
          LEFT JOIN "SearchDocument" d ON d."PageId"=p."Id" AND d."RevisionId"=p."PublishedRevisionId"
          CROSS JOIN actor a
        ), chosen AS MATERIALIZED (
          SELECT c.parent_id FROM candidate c CROSS JOIN actor a
          WHERE (c.parent_id IS NULL AND p_root_allowed)
            OR (c.parent_id IS NOT NULL
              AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
                WHERE e."UserId"=p_actor AND e."PageId"=c.parent_id AND e."Role">=2))
              AND EXISTS (SELECT 1 FROM step s WHERE s.candidate=c.parent_id)
              AND NOT EXISTS (SELECT 1 FROM step s WHERE s.candidate=c.parent_id AND NOT s.readable))
          ORDER BY c.votes DESC,c.best
          LIMIT 1
        )
        SELECT c.parent_id,
          COALESCE((SELECT array_agg(s.id ORDER BY s.depth DESC) FROM step s WHERE s.candidate=c.parent_id),'{}'::uuid[]),
          COALESCE((SELECT array_agg(s.title ORDER BY s.depth DESC) FROM step s WHERE s.candidate=c.parent_id),'{}'::text[])
        FROM chosen c
$$;

CREATE OR REPLACE FUNCTION atlas_template_suggestion(p_generation uuid, p_vector public.vector, p_title text, p_space uuid, p_actor uuid, p_stamp text, p_min_similarity real DEFAULT 0.75) RETURNS TABLE(template_id uuid, title text, matched_by text)
    LANGUAGE sql STABLE
    AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), dictionary(stem) AS (
          -- Tunable (design §7.8): runbook, postup, nasazení, kontakty, onboarding as stems of normalized words, so the
          -- Czech forms match too (postupu, nasazeni, kontakt).
          VALUES ('runbook'),('postup'),('nasazen'),('kontakt'),('onboarding')
        ), wanted AS MATERIALIZED (
          SELECT DISTINCT d.stem FROM dictionary d CROSS JOIN unnest(string_to_array(p_title,' ')) AS word
          WHERE word LIKE d.stem || '%'
        ), usable AS MATERIALIZED (
          SELECT p."Id",p."SortOrder",p."PublishedRevisionId",
            CASE WHEN a."IsWikiAdmin" OR e."Role">=2 THEN cur."Title" ELSE pub."Title" END::text AS title
          FROM "Page" p CROSS JOIN actor a
          LEFT JOIN "EffectivePageAccess" e ON e."UserId"=p_actor AND e."PageId"=p."Id"
          LEFT JOIN "PageRevision" pub ON pub."Id"=p."PublishedRevisionId"
          JOIN "PageRevision" cur ON cur."PageId"=p."Id" AND cur."ActiveTo" IS NULL
          WHERE p."SpaceId"=p_space AND p."IsTemplate" AND NOT p."IsTrashed"
            AND ((p."PublishedRevisionId" IS NOT NULL AND (a."IsWikiAdmin" OR e."Role">=1))
              OR (p."PublishedRevisionId" IS NULL AND (a."IsWikiAdmin" OR (e."Role">=2 AND p."OwnerId"=p_actor))))
        ), by_dictionary AS (
          SELECT u."Id",u.title,'dictionary' AS matched_by,0 AS tier,0::float8 AS similarity,u."SortOrder"
          FROM usable u
          WHERE EXISTS (SELECT 1 FROM wanted w CROSS JOIN unnest(string_to_array(atlas_normalize_search_text(u.title),' ')) AS word
            WHERE word LIKE w.stem || '%')
        ), by_meaning AS (
          SELECT u."Id",u.title,'meaning',1,(1-(k."TitleEmbedding" <=> p_vector))::float8,u."SortOrder"
          FROM usable u
          JOIN "KnowledgePage" k ON k."GenerationId"=p_generation AND k."PageId"=u."Id" AND k."RevisionId"=u."PublishedRevisionId"
          WHERE p_vector IS NOT NULL
            AND EXISTS (SELECT 1 FROM "IndexGeneration" g WHERE g."Id"=p_generation AND g."State"='Active')
            AND (SELECT atlas_space_in_ai(p_space))
            AND 1-(k."TitleEmbedding" <=> p_vector)>=p_min_similarity
        )
        SELECT x."Id",x.title,x.matched_by
        FROM (SELECT * FROM by_dictionary UNION ALL SELECT * FROM by_meaning) x
        ORDER BY x.tier,x.similarity DESC,x."SortOrder",x.title,x."Id"
        LIMIT 1
$$;

CREATE OR REPLACE FUNCTION atlas_answer_sources(p_actor uuid, p_stamp text, p_space uuid, p_page uuid, p_query text, p_prefix text, p_fts text, p_phrase boolean, p_generation uuid, p_embedding public.vector, p_min_similarity real, p_pages integer, p_take integer, p_per_page integer) RETURNS TABLE(chunk_ordinal smallint, generation_id uuid, page_id uuid, revision_id uuid, space_id uuid, space_name text, title text, heading_id text, heading_title text, text text, rank integer)
    LANGUAGE sql STABLE
    AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), mode AS MATERIALIZED (
          -- The generation the text is read from, and whether the vector belongs to it (one snapshot of the pair, spec RAG §6).
          SELECT g."Id" AS generation,(p_embedding IS NOT NULL AND g."Id"=p_generation) AS semantic
          FROM "IndexGeneration" g WHERE g."State"='Active'
        ), candidate AS MATERIALIZED (
          SELECT c.page_id,c.lexical_score,c.lexical_rank,c.vector_rank
          FROM atlas_search_hybrid_candidates(p_query,p_prefix,p_fts,p_phrase,false,p_actor,p_stamp,p_space,300,NULL,false,
            CASE WHEN p_embedding IS NULL THEN NULL ELSE p_generation END,p_embedding,p_min_similarity) c
          WHERE p_page IS NULL
          UNION ALL
          SELECT p_page,NULL::real,NULL::integer,NULL::integer WHERE p_page IS NOT NULL
        ), eligible AS MATERIALIZED (
          SELECT p."Id",p."SpaceId",p."PublishedRevisionId",r."Title"::text AS title,c.lexical_score,c.lexical_rank,c.vector_rank
          FROM candidate c JOIN "Page" p ON p."Id"=c.page_id
            JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId" CROSS JOIN actor a CROSS JOIN mode m
          WHERE NOT p."IsTrashed" AND NOT p."IsTemplate"
            AND (p_space IS NULL OR p."SpaceId"=p_space)
            AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e WHERE e."UserId"=p_actor AND e."PageId"=p."Id" AND e."Role">=1))
            AND EXISTS (SELECT 1 FROM "KnowledgePage" k WHERE k."GenerationId"=m.generation AND k."PageId"=p."Id" AND k."RevisionId"=p."PublishedRevisionId")
        ), included AS MATERIALIZED (
          SELECT s.id FROM (SELECT DISTINCT e."SpaceId" AS id FROM eligible e) s WHERE atlas_space_in_ai(s.id)
        ), page AS MATERIALIZED (
          SELECT e.*,(row_number() OVER (ORDER BY CASE WHEN e.lexical_score>=1500 THEN e.lexical_score END DESC NULLS LAST,
              COALESCE(1.0::float8/(60+e.vector_rank),0)+COALESCE(0.5::float8/(60+e.lexical_rank),0) DESC,e.title,e."Id"))::integer AS position
          FROM eligible e WHERE e."SpaceId" IN (SELECT id FROM included)
          ORDER BY position LIMIT p_pages
        ), chunk AS MATERIALIZED (
          SELECT pg.position,pg.lexical_score,pg."Id" AS page_id,pg."SpaceId",pg."PublishedRevisionId" AS revision_id,pg.title,
            k."GenerationId",k."Ordinal",k."HeadingId",k."HeadingTitle",k."Text",
            CASE WHEN m.semantic THEN k."Embedding" <=> p_embedding END AS distance,
            CASE WHEN NOT m.semantic THEN ts_rank_cd(to_tsvector('simple',atlas_normalize_search_text(
              COALESCE(k."HeadingTitle",'')||' '||substr(k."Text",strpos(k."Text",E'\n')+1))),to_tsquery('simple',p_fts)) END AS lexical
          FROM page pg CROSS JOIN mode m
            JOIN "KnowledgeChunk" k ON k."GenerationId"=m.generation AND k."PageId"=pg."Id" AND k."RevisionId"=pg."PublishedRevisionId"
          WHERE strpos(k."Text",E'\n')>0
        ), anchor AS MATERIALIZED (
          -- The best matching passage on each authorized, current page is the anchor of a short-section window.
          SELECT DISTINCT ON (c.page_id) c.page_id,c."Ordinal",
            length(substr(c."Text",strpos(c."Text",E'\n')+1)) AS body_length
          FROM chunk c
          ORDER BY c.page_id,c.distance NULLS LAST,c.lexical DESC NULLS LAST,c."Ordinal"
        ), focus AS MATERIALIZED (
          -- Short sections on a longer page need their adjacent sections to answer a broad question. Each
          -- neighbor stays a separate citable chunk of the same already-authorized published revision.
          SELECT a.page_id,a."Ordinal" AS anchor_ordinal,GREATEST(sz.first_ordinal,LEAST(a."Ordinal",sz.last_ordinal-3)) AS window_start
          FROM anchor a JOIN (SELECT page_id,count(*) AS chunk_count,min("Ordinal") AS first_ordinal,max("Ordinal") AS last_ordinal FROM chunk GROUP BY page_id) sz USING (page_id)
            JOIN page pg ON pg."Id"=a.page_id
          WHERE p_page IS NULL AND a.body_length<300 AND sz.chunk_count>=4
            AND to_tsvector('simple',atlas_normalize_search_text(pg.title)) @@ to_tsquery('simple',p_fts)
        ), numbered AS MATERIALIZED (
          SELECT c.*,f.anchor_ordinal,f.window_start,(f.page_id IS NOT NULL) AS focused,
            row_number() OVER (PARTITION BY c.page_id ORDER BY
              CASE WHEN f.page_id IS NOT NULL AND c."Ordinal" BETWEEN f.window_start AND f.window_start+3
                THEN abs(c."Ordinal"-f.anchor_ordinal) END NULLS LAST,
              CASE WHEN f.page_id IS NOT NULL AND c."Ordinal" BETWEEN f.window_start AND f.window_start+3
                THEN CASE WHEN c."Ordinal">=f.anchor_ordinal THEN 0 ELSE 1 END END NULLS LAST,
              c.distance NULLS LAST,c.lexical DESC NULLS LAST,c."Ordinal") AS nth
          FROM chunk c LEFT JOIN focus f ON f.page_id=c.page_id
        ), chosen AS MATERIALIZED (
          SELECT n.* FROM numbered n
          WHERE (n.focused AND n.nth<=4
              AND n."Ordinal" BETWEEN n.window_start AND n.window_start+3)
            OR (NOT n.focused AND n.nth<=CASE WHEN p_page IS NULL THEN p_per_page ELSE p_take END
              AND (n.nth=1 OR p_page IS NOT NULL OR 1-n.distance>=p_min_similarity OR n.lexical>0))
        )
        SELECT n."Ordinal",n."GenerationId",n.page_id,n.revision_id,n."SpaceId",s."Name"::text,n.title,
          n."HeadingId"::text,n."HeadingTitle"::text,
          atlas_strip_secret_directives(substr(n."Text",strpos(n."Text",E'\n')+1)),
          (row_number() OVER (ORDER BY CASE WHEN n.lexical_score>=1500 THEN n.lexical_score END DESC NULLS LAST,
            CASE WHEN n.focused THEN 0 ELSE 1 END,n.position,n.nth))::integer
        FROM chosen n JOIN "Space" s ON s."Id"=n."SpaceId"
        ORDER BY CASE WHEN n.lexical_score>=1500 THEN n.lexical_score END DESC NULLS LAST,
            CASE WHEN n.focused THEN 0 ELSE 1 END,n.position,n.nth
        LIMIT p_take
$$;

CREATE OR REPLACE FUNCTION atlas_answer_source_status(p_actor uuid, p_stamp text, p_pages uuid[], p_revisions uuid[]) RETURNS TABLE(page_id uuid, revision_id uuid, available boolean, changed boolean)
    LANGUAGE sql STABLE
    AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), ref AS MATERIALIZED (
          SELECT DISTINCT r.page_id,r.revision_id FROM unnest(p_pages,p_revisions) AS r(page_id,revision_id)
        ), readable AS MATERIALIZED (
          SELECT r.page_id,r.revision_id,p."SpaceId",p."PublishedRevisionId"
          FROM ref r JOIN "Page" p ON p."Id"=r.page_id CROSS JOIN actor a
          WHERE NOT p."IsTrashed" AND NOT p."IsTemplate" AND p."PublishedRevisionId" IS NOT NULL
            AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e WHERE e."UserId"=p_actor AND e."PageId"=p."Id" AND e."Role">=1))
        ), included AS MATERIALIZED (
          SELECT s.id FROM (SELECT DISTINCT x."SpaceId" AS id FROM readable x) s WHERE atlas_space_in_ai(s.id)
        )
        SELECT r.page_id,r.revision_id,x.page_id IS NOT NULL,x.page_id IS NOT NULL AND x."PublishedRevisionId"<>r.revision_id
        FROM ref r LEFT JOIN readable x ON x.page_id=r.page_id AND x.revision_id=r.revision_id
          AND x."SpaceId" IN (SELECT id FROM included)
$$;
