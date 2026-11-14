-- Atlas 2.0 V5: E2 link candidates, E4 [[ ordering by the surrounding section, and the vector part of P1–P3 (plan phase 2
-- §13, design §7.5, §7.8). Read-only functions over the projections of migrations 2026100540 (knowledge index) and 027
-- (SearchDocument); nothing new is stored.
--
-- Security (plan §3, spec RAG §3, §6), the same rule in every function:
--   * the actor is active with its current stamp (CTE actor), and reads a page only with EffectivePageAccess role >= 1 or as
--     WikiAdmin; a hidden page leaves no title, row or count;
--   * only published content: the page's current PublishedRevisionId, never a draft, outside the trash, not a template
--     (P3 reads templates, by the template listing rule);
--   * a vector read uses the given generation only while it is Active (one snapshot of the pair), the index row of the live
--     publication (KnowledgePage.RevisionId = Page.PublishedRevisionId) and pages of spaces included in AI
--     (atlas_space_in_ai, evaluated once per space, plan §14 note);
--   * the callers pass vectors of the author's working text, made before the transaction (plan §1.1); nothing here calls a
--     model, and nothing here is logged.
--
-- Tunable constants (O17, step M of the plan tunes them, tests pin them): E2 title similarity 0.80, P1 similarity 0.75 (the
-- better of title and content), P2 similarity 0.45 and its 5 nearest pages, P3 template title similarity 0.75, the HNSW
-- candidate depths (5 per E2 phrase, 40 per P1 index), at most 3 exact candidates per E2 phrase, and the P3 dictionary.
-- The vector parameters are the integer text of QueryVectorText (cosine distance ignores the scale).

-- E2, lexical part: an exact match of a normalized phrase with the normalized title, or with a normalized alias (a previous
-- published title or an import path), of a published page the actor may read. The page being written (p_exclude) never.
-- At most 3 pages per phrase: a title match before an alias, the page's own space (p_space) first, then by title.
CREATE FUNCTION atlas_link_candidates(p_phrases text[], p_actor uuid, p_stamp text, p_exclude uuid, p_space uuid)
RETURNS TABLE(phrase text, page_id uuid, space_id uuid, space_name text, title text, matched_by text)
LANGUAGE sql STABLE AS $$
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

-- E2, vector part: for each multi-word phrase (p_phrases[i] embedded as p_vectors[i]) the nearest title of a readable
-- published page of the active generation, kept only at p_min_similarity or more (0.80). The HNSW scan of the title index
-- filters every scanned row by the access rule (iterative scan, set by the caller), as hybrid search does.
CREATE FUNCTION atlas_link_candidates_semantic(p_generation uuid, p_phrases text[], p_vectors vector[], p_actor uuid,
  p_stamp text, p_exclude uuid, p_min_similarity real DEFAULT 0.80)
RETURNS TABLE(phrase text, page_id uuid, space_id uuid, space_name text, title text, similarity real)
LANGUAGE sql STABLE AS $$
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

-- E4: the cosine similarity of the section around the cursor (p_vector) to the content of each candidate (its centroid,
-- or its title vector when it has no chunk). Only readable published candidates of the active generation in spaces
-- included in AI come back; any other candidate (a draft, a hidden page, an excluded space) is simply absent and keeps its
-- lexical place in the client.
CREATE FUNCTION atlas_rank_by_context(p_generation uuid, p_vector vector, p_candidates uuid[], p_actor uuid, p_stamp text)
RETURNS TABLE(page_id uuid, similarity real)
LANGUAGE sql STABLE AS $$
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

-- P1, vector part: published pages the actor may read whose title or content is near the title being typed. The score is
-- the better of the title and the centroid similarity, kept at p_min_similarity or more (0.75). Drafts never (the index
-- holds publications only); the caller puts these after the lexical suggestions and drops pages it already has.
CREATE FUNCTION atlas_similar_pages_semantic(p_generation uuid, p_vector vector, p_actor uuid, p_stamp text, p_space uuid,
  p_exclude uuid, p_take integer, p_min_similarity real DEFAULT 0.75)
RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, score real)
LANGUAGE sql STABLE AS $$
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

-- P2: where a new page with this title belongs. The 5 readable published pages of the space nearest to the title (the
-- better of title and content similarity, at least p_min_similarity = 0.45; exact over the space's pages, one space is
-- bounded) vote with their parent; the root of the space is a parent too (NULL). A parent is offered only where the actor
-- may create (EditContent = role >= 2 on the parent, or WikiAdmin; the root only with p_root_allowed, the caller's Writer
-- authority on the space, as CreatePage decides) and only when the parent and every ancestor are readable publications, so
-- the path names nothing the actor may not read. Most votes win, then the nearest page. One row (the root: parent_id NULL
-- and empty arrays) or none; path_ids/path_titles run from the top of the space down to the parent itself.
CREATE FUNCTION atlas_placement_parent(p_generation uuid, p_vector vector, p_space uuid, p_actor uuid, p_stamp text,
  p_root_allowed boolean, p_min_similarity real DEFAULT 0.45)
RETURNS TABLE(parent_id uuid, path_ids uuid[], path_titles text[])
LANGUAGE sql STABLE AS $$
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

-- P3: a template of the space for a page with this (normalized) title. The templates are those the actor may use, by the
-- template listing rule (PageListing.Listed): a published one the actor may read, or an unpublished one the actor may edit
-- and owns (any, as WikiAdmin); the title an editor sees is the working one. First the dictionary, with or without AI: a
-- word of the title starting with one of the stems below, and a template whose title has a word with the same stem.
-- Then, only with a vector (p_vector not NULL), the nearest published template title of the active generation at
-- p_min_similarity (0.75) or more, in a space included in AI. Templates in their order in the space, then by title.
CREATE FUNCTION atlas_template_suggestion(p_generation uuid, p_vector vector, p_title text, p_space uuid, p_actor uuid,
  p_stamp text, p_min_similarity real DEFAULT 0.75)
RETURNS TABLE(template_id uuid, title text, matched_by text)
LANGUAGE sql STABLE AS $$
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

-- Whether the actor may create a page somewhere in the space below an existing page (EditContent on a live page that is
-- not a template, or WikiAdmin); the root of the space is the caller's Writer authority. P2 and P3 offer nothing elsewhere.
CREATE FUNCTION atlas_may_create_below_page(p_space uuid, p_actor uuid, p_stamp text)
RETURNS boolean
LANGUAGE sql STABLE AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        )
        SELECT EXISTS (SELECT 1 FROM actor a WHERE a."IsWikiAdmin" AND EXISTS (SELECT 1 FROM "Space" s WHERE s."Id"=p_space))
          OR EXISTS (SELECT 1 FROM actor a CROSS JOIN "EffectivePageAccess" e JOIN "Page" p ON p."Id"=e."PageId"
            WHERE e."UserId"=p_actor AND e."SpaceId"=p_space AND e."Role">=2 AND p."SpaceId"=p_space
              AND NOT p."IsTrashed" AND NOT p."IsTemplate")
$$;
