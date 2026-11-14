-- Search understands Czech word forms and code identifiers (2026-10-20, the owner's poor-recall reports). New migration only;
-- historical function definitions remain immutable.
--
-- The 'simple' vectors held only the exact normalized words: "smlouva" missed "smlouvy" and "smlouvou", and getFreeBusy was
-- the single lexeme getfreebusy, so neither "free busy" nor "freebusy" found it. Every searchable text (title, headings, body,
-- aliases; published and working) now gets extra lexemes beside its exact ones:
-- 1. identifier parts: a camelCase, PascalCase, snake_case, kebab-case or dotted token (letters and digits joined by _ - .,
--    starting with a letter; paths, links and URLs split at their other punctuation first) gives its parts and each join of
--    its trailing parts (getFreeBusy: get free busy getfreebusy freebusy), so a prefix query on any run of parts matches;
-- 2. the light Czech stem of every word that has a case ending (atlas_search_stem, twin of SearchText.Stem), at the positions
--    of that word: a stem list of its own would put unrelated stems side by side and make ts_rank_cd see proximity that is not
--    in the text (measured on the real wiki: inflected pages overtook the exact ones).
-- A query word w becomes (w:* | stem) in C# (SearchText.QueryExpression); a document with the exact form matches both and
-- ranks higher. Quoted phrases stay exact. NormalizedTitle and NormalizedBody (equality, prefix and trigram tiers) are unchanged.

-- shortcut: case/number endings only (no verbs, no consonant alternations like ruka/ruce), upgrade with a real stemmer if needed.
-- The lazy prefix makes the longest ending win, as the ordered list in SearchText.Stem.
CREATE FUNCTION atlas_search_stem(word text) RETURNS text LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
  SELECT COALESCE(substring(word from '^(.{4,}?)(?:ami|ach|ich|ymi|ech|um|ou|em|im|am|a|u|y|e|i|o)$'), word)
$$;

-- The identifier words of a raw text: per identifier its parts, then the joins of its trailing parts.
-- shortcut: the last 8 parts of an identifier only, enough for API paths and code names.
CREATE FUNCTION atlas_search_identifiers(raw text) RETURNS text LANGUAGE sql STABLE STRICT PARALLEL SAFE AS $$
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

-- The lexemes of a searchable text (raw: for case boundaries; normalized: atlas_normalize_search_text of it): its exact words
-- with their stems at the same positions, then its identifier words (positions after the text, so phrases stay exact).
-- The text form of a tsvector keeps at most 256 positions per lexeme, as to_tsvector does.
CREATE FUNCTION atlas_search_lexemes(raw text, normalized text) RETURNS tsvector LANGUAGE sql STABLE STRICT PARALLEL SAFE AS $$
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

CREATE OR REPLACE FUNCTION atlas_update_search_structure() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE heading jsonb; boundary tsvector := '''__scaleatlas_field_boundary__'':1'::tsvector;
BEGIN
  SELECT atlas_search_headings(atlas_strip_secret_directives(r."Markdown"),r."ContentJson"),
    COALESCE((SELECT v."ActiveFrom" FROM "PagePublication" v WHERE v."PageId"=NEW."PageId" AND v."ActiveTo" IS NULL),r."ActiveFrom")
  INTO NEW."Headings",NEW."PublishedAt" FROM "PageRevision" r WHERE r."Id"=NEW."RevisionId";
  NEW."HeadingVector" := ''::tsvector;
  FOR heading IN SELECT value FROM jsonb_array_elements(NEW."Headings") LOOP
    NEW."HeadingVector" := NEW."HeadingVector" || boundary || atlas_search_lexemes(heading->>'title',heading->>'normalizedTitle');
  END LOOP;
  NEW."HeadingVector" := ts_delete(NEW."HeadingVector",'__scaleatlas_field_boundary__');
  NEW."Vector" := ts_delete(setweight(atlas_search_lexemes(NEW."Title",NEW."NormalizedTitle"),'A') || boundary
    || setweight(NEW."HeadingVector",'B') || boundary || setweight(atlas_search_lexemes(NEW."Body",NEW."NormalizedBody"),'D'),
    '__scaleatlas_field_boundary__');
  RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION atlas_update_search_aliases() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE alias_text text; normalized text; boundary tsvector := '''__scaleatlas_alias_boundary__'':1'::tsvector;
BEGIN
  NEW."Aliases" := '[]'::jsonb;
  NEW."AliasVector" := ''::tsvector;
  -- Read at most 32 recent publication records using the page/history index, never the working history.
  FOR alias_text IN
    SELECT r."Title" FROM (
      SELECT "RevisionId" FROM "PagePublication" WHERE "PageId"=NEW."PageId"
      ORDER BY "ActiveFrom" DESC LIMIT 32
    ) publication JOIN "PageRevision" r ON r."Id"=publication."RevisionId"
  LOOP
    normalized := atlas_normalize_search_text(alias_text);
    IF normalized<>'' AND normalized<>NEW."NormalizedTitle" AND NOT NEW."Aliases" @> jsonb_build_array(alias_text) THEN
      NEW."Aliases" := NEW."Aliases" || jsonb_build_array(alias_text);
      NEW."AliasVector" := NEW."AliasVector" || boundary || atlas_search_lexemes(alias_text,normalized);
    END IF;
  END LOOP;
  -- A newly imported draft of an already public page must not reveal its source path.
  -- Keep a path once one of its exact import checkpoints really was published, including later user edits.
  FOR alias_text IN
    SELECT m."SourcePath" FROM "WikiImportPage" m WHERE m."PageId"=NEW."PageId" AND EXISTS (
      SELECT 1 FROM "WikiImportCheckpoint" c JOIN "PagePublication" v
        ON v."PageId"=m."PageId" AND v."RevisionId"=c."RevisionId"
      WHERE c."ImportPageId"=m."Id")
    ORDER BY m."SourcePath",m."Id" LIMIT 16
  LOOP
    normalized := atlas_normalize_search_text(alias_text);
    IF normalized<>'' AND NOT NEW."Aliases" @> jsonb_build_array(alias_text) THEN
      NEW."Aliases" := NEW."Aliases" || jsonb_build_array(alias_text);
      NEW."AliasVector" := NEW."AliasVector" || boundary || atlas_search_lexemes(alias_text,normalized);
    END IF;
  END LOOP;
  -- Keep positional gaps after removing the unqueryable marker; quoted phrases cannot cross aliases.
  NEW."AliasVector" := ts_delete(NEW."AliasVector",'__scaleatlas_alias_boundary__');
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
    NEW."HeadingVector" := NEW."HeadingVector" || boundary || atlas_search_lexemes(heading->>'title',heading->>'normalizedTitle');
  END LOOP;
  NEW."HeadingVector" := ts_delete(NEW."HeadingVector",'__scaleatlas_field_boundary__');
  NEW."Vector" := ts_delete(setweight(atlas_search_lexemes(source."Title",NEW."NormalizedTitle"),'A') || boundary
    || setweight(NEW."HeadingVector",'B') || boundary || setweight(atlas_search_lexemes(body,NEW."NormalizedBody"),'D'),
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
            AND (atlas_search_lexemes(heading->>'title',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts)
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE atlas_search_lexemes(alias,atlas_normalize_search_text(alias)) @@ to_tsquery('simple',p_fts)
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
            AND (atlas_search_lexemes(heading->>'title',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts)
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE atlas_search_lexemes(alias,atlas_normalize_search_text(alias)) @@ to_tsquery('simple',p_fts)
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
            AND (atlas_search_lexemes(heading->>'title',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts)
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        ORDER BY CASE WHEN p_latest THEN r."ChangedAt" END DESC NULLS LAST,r.score DESC,r."Title",r."PageId"
$$;

CREATE OR REPLACE FUNCTION atlas_search_hybrid(
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

-- Existing rows: naming the columns fires the structure and alias triggers (published) and the projection (working).
UPDATE "SearchDocument" SET "NormalizedTitle"="NormalizedTitle";
UPDATE "WorkingSearchDocument" SET "RevisionId"="RevisionId";
