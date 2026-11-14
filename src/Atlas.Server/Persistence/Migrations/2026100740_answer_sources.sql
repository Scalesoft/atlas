-- Atlas 2.0 Q2 (plan phase 3 §6, R3; spec RAG §1 AI-11, §5, §6): the sources of an answer from the wiki and their status.
--
-- atlas_answer_sources picks pages the way the phase-2 hybrid search ranks them, then chunks inside them:
--   * pages: atlas_search_hybrid_candidates (migration 2026100550) with the question's vector when the endpoint has one, else
--     its lexical part alone (the published ranking of search). The caller passes the question's significant words OR'ed
--     (AnswerQueryText): a question is a sentence and needs no word of it to match. Ordered as V3: title tiers >= 1500 first,
--     then 1/(60 + vector rank) + 0.5/(60 + lexical rank); the first p_pages eligible pages. A page scope (p_page) reads that
--     page alone, with or without a match;
--   * chunks: of the Active generation and the page's current published revision only. With a vector for the Active
--     generation by distance, else by ts_rank_cd over the chunk's normalized heading and body (then by Ordinal). At most
--     p_per_page of one page (a page scope: up to p_take of it), a page's further chunks only when they match on their own
--     (similarity >= p_min_similarity, or a lexical rank > 0); p_take in all, page by page (page -> chunk);
--   * text: the chunk body without its context line "{title} › {heading}" (title and heading are columns of their own), with
--     password references removed once more (the index never stores them, V2; this is the second line, spec RAG §1). A chunk
--     without a body (a page of a title only) holds nothing to cite and is skipped.
--
-- Security (spec RAG §5 points 2–4, plan phase 3 §3): every page passes the published rule of search inside this statement:
-- the actor active with its current stamp, EffectivePageAccess role >= 1 or WikiAdmin, the live page's current published
-- revision outside the trash, not a template, in a space included in AI (atlas_space_in_ai once per space, finding V6) and
-- indexed for that revision in the Active generation. The vector candidates of atlas_search_hybrid_candidates apply the same
-- rule to every scanned chunk (HNSW iterative scan, AnswerSourceSql), so a hidden page never takes a page slot or a source
-- slot. Nothing of an unreadable page is returned, not even as a count. Drafts never: Writers and WikiAdmins included.
--
-- Tunable constants (step M of the plan tunes them, AnswerSourcesTests pin them): the caller's p_pages 8, p_take 6,
-- p_per_page 2 and p_min_similarity 0.45; the lexical window 300 and the fusion of V3.
CREATE FUNCTION atlas_answer_sources(
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
          SELECT pg.position,pg."Id" AS page_id,pg."SpaceId",pg."PublishedRevisionId" AS revision_id,pg.title,
            k."GenerationId",k."Ordinal",k."HeadingId",k."HeadingTitle",k."Text",
            CASE WHEN m.semantic THEN k."Embedding" <=> p_embedding END AS distance,
            CASE WHEN NOT m.semantic THEN ts_rank_cd(to_tsvector('simple',atlas_normalize_search_text(
              COALESCE(k."HeadingTitle",'')||' '||substr(k."Text",strpos(k."Text",E'\n')+1))),to_tsquery('simple',p_fts)) END AS lexical
          FROM page pg CROSS JOIN mode m
            JOIN "KnowledgeChunk" k ON k."GenerationId"=m.generation AND k."PageId"=pg."Id" AND k."RevisionId"=pg."PublishedRevisionId"
          WHERE strpos(k."Text",E'\n')>0
        ), numbered AS MATERIALIZED (
          SELECT c.*,row_number() OVER (PARTITION BY c.page_id ORDER BY c.distance NULLS LAST,c.lexical DESC NULLS LAST,c."Ordinal") AS nth
          FROM chunk c
        ), chosen AS MATERIALIZED (
          SELECT n.* FROM numbered n
          WHERE n.nth<=CASE WHEN p_page IS NULL THEN p_per_page ELSE p_take END
            AND (n.nth=1 OR p_page IS NOT NULL OR 1-n.distance>=p_min_similarity OR n.lexical>0)
        )
        SELECT n."Ordinal",n."GenerationId",n.page_id,n.revision_id,n."SpaceId",s."Name"::text,n.title,
          n."HeadingId"::text,n."HeadingTitle"::text,
          atlas_strip_secret_directives(substr(n."Text",strpos(n."Text",E'\n')+1)),
          (row_number() OVER (ORDER BY n.position,n.nth))::integer
        FROM chosen n JOIN "Space" s ON s."Id"=n."SpaceId"
        ORDER BY n.position,n.nth
        LIMIT p_take
$$;

-- The status of stored page sources (the page adapter's StatusAsync, history reads of Q4): the same published rule as above,
-- in one statement, without locks. available = the actor may read the page's published content now; changed = available,
-- but the published revision is no longer the referenced one. A missing page is unavailable.
CREATE FUNCTION atlas_answer_source_status(p_actor uuid, p_stamp text, p_pages uuid[], p_revisions uuid[])
RETURNS TABLE(page_id uuid, revision_id uuid, available boolean, changed boolean)
LANGUAGE sql STABLE AS $$
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
