-- Atlas 2.0 P1: pages similar to the title of a page being created. Search requires every word (AND), so a longer new title
-- ("Připojení na VPN z mobilu") never finds the existing page ("Připojení k VPN"); this function scores title similarity over
-- the same projections and the same ACL as atlas_search_*, and leaves search ranking unchanged.
-- Readable scope: published pages (SearchDocument, the published revision, role >= 1 or WikiAdmin) and the actor's own
-- never-published drafts (WorkingSearchDocument, owner with role >= 2; a WikiAdmin sees only their own too). Trash and
-- templates never count as a duplicate. A hidden page leaves no title, row or count: the ACL filters candidates before scoring.
-- The caller sets pg_trgm.similarity_threshold = 0.3 for the trigram pre-selection (SearchSql.PrepareSimilarAsync).
-- p_words is the OR of the significant words (SearchText.AnyWordQuery); it may be empty.
-- Tunable constants (pinned by SimilarPagesTests): result threshold 0.35; a section needs a normalized heading of >= 6
-- characters contained in the title (word_similarity >= 0.8); a page matching only by a section needs similarity >= 0.5.
-- New migration only; historical function definitions remain immutable.

CREATE FUNCTION atlas_similar_pages(
  p_query text, p_words text, p_actor uuid, p_stamp text, p_space uuid, p_exclude uuid, p_take integer)
RETURNS TABLE(page_id uuid, space_id uuid, space_name text, title text, is_draft boolean,
              matched_by text, heading_title text, heading_id text, score real)
LANGUAGE sql STABLE AS $$
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
