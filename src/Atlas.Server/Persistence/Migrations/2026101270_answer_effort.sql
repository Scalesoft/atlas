-- Answer effort (AnswerEffort): p_per_page is now the caller's limit of chunks of one page outside a focused short-section
-- window (it was capped at two), so a thorough or deep answer reads more sections of the same page. A focused window keeps up to
-- four adjacent chunks whatever p_per_page is. The quick effort passes two and selects exactly what 2026100860 selected.
-- Everything else is 2026100860 unchanged.
CREATE OR REPLACE FUNCTION atlas_answer_sources(
  p_actor uuid, p_stamp text, p_space uuid, p_page uuid,
  p_query text, p_prefix text, p_fts text, p_phrase boolean,
  p_generation uuid, p_embedding vector, p_min_similarity real,
  p_pages integer, p_take integer, p_per_page integer)
RETURNS TABLE(chunk_ordinal smallint, generation_id uuid, page_id uuid, revision_id uuid, space_id uuid, space_name text,
              title text, heading_id text, heading_title text, text text, rank integer)
LANGUAGE sql STABLE AS $$
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