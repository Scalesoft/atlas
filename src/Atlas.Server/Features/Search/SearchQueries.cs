using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Identity;

namespace Atlas.Server.Features.Search;

/// <summary>
/// The search queries as C# text (Atlas 3.6, no logic in the database): each one the body of the function it replaced
/// (atlas_search_published, atlas_search_published_filtered, atlas_search_drafts, atlas_search_all, atlas_search_spaces,
/// atlas_search_did_you_mean, atlas_search_hybrid_candidates, atlas_search_hybrid, atlas_search_hybrid_all,
/// atlas_search_hybrid_published, atlas_similar_pages, atlas_similar_pages_semantic), with the same ranking, access filters and
/// output columns, its arguments named parameters without the <c>p_</c> (:query, :changed_since, :min_similarity…). A vector
/// travels as text and is cast where it is read. <c>SELECT * FROM (text) x</c> returns what the function returned
/// (SearchParityTests). <see cref="SearchSql"/> binds them.
/// </summary>
public static class SearchQueries
{
    /// <summary>The published scope (atlas_search_published).</summary>
    public static readonly string Published = $$"""
        WITH {{AccessSql.ActorCte}}, access AS MATERIALIZED (
          SELECT e."PageId" FROM "EffectivePageAccess" e
          WHERE e."UserId"=:actor AND (:space IS NULL OR e."SpaceId"=:space)
        ), strict AS MATERIALIZED (
          SELECT count(*) AS hits FROM (
            SELECT 1 FROM "SearchDocument" d JOIN "Page" p ON p."Id"=d."PageId" CROSS JOIN actor a
            WHERE :fuzzy AND NOT p."IsTrashed" AND p."PublishedRevisionId"=d."RevisionId"
              AND (:space IS NULL OR d."SpaceId"=:space)
              AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access))
              AND (d."Vector" @@ to_tsquery('simple',:fts) OR d."AliasVector" @@ to_tsquery('simple',:fts) OR (NOT :phrase AND d."NormalizedTitle" LIKE :prefix)
                OR :query <% d."NormalizedTitle")
            LIMIT :skip+:take) found
        ), body AS MATERIALIZED (
          SELECT d."PageId" FROM "SearchDocument" d
          WHERE :fuzzy AND (SELECT hits FROM strict)<:skip+:take AND :query <% left(d."NormalizedBody",20000)
        ), ranked AS MATERIALIZED (
          SELECT d."PageId",d."SpaceId",d."Title",d."PublishedAt",CAST(s."Name" AS text) AS "SpaceName",
            CASE WHEN d."NormalizedTitle"=:query THEN 10000
                 WHEN d."NormalizedTitle" LIKE :prefix THEN 5000
                 WHEN ts_filter(d."Vector",'{a}') @@ to_tsquery('simple',:fts) THEN 3000
                 WHEN d."HeadingVector" @@ to_tsquery('simple',:fts) THEN 2000
                 WHEN d."AliasVector" @@ to_tsquery('simple',:fts) THEN 2500
                 WHEN d."Vector" @@ to_tsquery('simple',:fts) THEN 1000
                 WHEN :query <% d."NormalizedTitle" THEN 500 ELSE 0 END
              + 100 * ts_rank_cd(d."Vector",to_tsquery('simple',:fts),32)
              + 50 * word_similarity(:query,d."NormalizedTitle") AS score
          FROM "SearchDocument" d JOIN "Space" s ON s."Id"=d."SpaceId"
            JOIN "Page" p ON p."Id"=d."PageId" CROSS JOIN actor a
          WHERE NOT p."IsTrashed" AND p."PublishedRevisionId"=d."RevisionId"
            AND (:space IS NULL OR d."SpaceId"=:space)
            AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access))
            AND (d."Vector" @@ to_tsquery('simple',:fts) OR d."AliasVector" @@ to_tsquery('simple',:fts) OR (NOT :phrase AND d."NormalizedTitle" LIKE :prefix)
              OR (:fuzzy AND (:query <% d."NormalizedTitle"
                OR d."PageId" IN (SELECT "PageId" FROM body))))
          ORDER BY score DESC,d."Title",d."PageId" LIMIT :take OFFSET :skip
        )
        SELECT r."PageId" AS page_id,r."SpaceId" AS space_id,r."SpaceName" AS space_name,r."Title" AS title,
          left(ts_headline('simple',substr(d."Body",greatest(coalesce(nullif(strpos(d."NormalizedBody",:query),0),strpos(d."NormalizedBody",split_part(:query,' ',1)))-10000,1),20000),
            to_tsquery('simple',:fts),
            'StartSel="", StopSel="", MaxWords=35, MinWords=15, MaxFragments=1'),2000) AS snippet,r."PublishedAt" AS changed_at,
          h.heading->>'title' AS heading_title,h.heading->>'headingId' AS heading_id,matched.alias AS matched_alias
        FROM ranked r JOIN "SearchDocument" d ON d."PageId"=r."PageId"
        LEFT JOIN LATERAL (
          SELECT heading FROM jsonb_array_elements(d."Headings") AS heading
          WHERE heading->>'headingId' IS NOT NULL
            AND (((:phrase OR d."HeadingVector" @@ to_tsquery('simple',:fts))
                AND {{SearchSqlText.Lexemes("heading->>'title'", "heading->>'normalizedTitle'")}} @@ to_tsquery('simple',:fts))
              OR (:fuzzy AND :query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=:query THEN 2 ELSE 1 END DESC,
            word_similarity(:query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE (:phrase OR d."AliasVector" @@ to_tsquery('simple',:fts))
            AND {{SearchSqlText.Lexemes("alias", SearchSqlText.Normalize("alias"))}} @@ to_tsquery('simple',:fts)
          ORDER BY alias LIMIT 1
        ) matched ON true
        ORDER BY r.score DESC,r."Title",r."PageId"
        """;

    /// <summary>The published scope with a time filter and the newest first on demand (atlas_search_published_filtered); with
    /// <paramref name="take"/> and <paramref name="skip"/> as SQL expressions for the unified scope.</summary>
    public static string PublishedFiltered(string take = ":take", string skip = ":skip") => $$"""
        WITH {{AccessSql.ActorCte}}, access AS MATERIALIZED (
          SELECT e."PageId" FROM "EffectivePageAccess" e
          WHERE e."UserId"=:actor AND (:space IS NULL OR e."SpaceId"=:space)
        ), strict AS MATERIALIZED (
          SELECT count(*) AS hits FROM (
            SELECT 1 FROM "SearchDocument" d JOIN "Page" p ON p."Id"=d."PageId" CROSS JOIN actor a
            WHERE :fuzzy AND NOT :latest AND NOT p."IsTrashed" AND p."PublishedRevisionId"=d."RevisionId"
              AND (:space IS NULL OR d."SpaceId"=:space)
              AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access))
              AND (d."Vector" @@ to_tsquery('simple',:fts) OR d."AliasVector" @@ to_tsquery('simple',:fts) OR (NOT :phrase AND d."NormalizedTitle" LIKE :prefix)
                OR :query <% d."NormalizedTitle")
              AND (:changed_since IS NULL OR d."PublishedAt">=:changed_since)
            LIMIT {{skip}}+{{take}}) found
        ), body AS MATERIALIZED (
          SELECT d."PageId" FROM "SearchDocument" d
          WHERE :fuzzy AND (:latest OR (SELECT hits FROM strict)<{{skip}}+{{take}}) AND :query <% left(d."NormalizedBody",20000)
        ), ranked AS MATERIALIZED (
          SELECT d."PageId",d."SpaceId",d."Title",d."PublishedAt",CAST(s."Name" AS text) AS "SpaceName",
            CASE WHEN d."NormalizedTitle"=:query THEN 10000
                 WHEN d."NormalizedTitle" LIKE :prefix THEN 5000
                 WHEN ts_filter(d."Vector",'{a}') @@ to_tsquery('simple',:fts) THEN 3000
                 WHEN d."HeadingVector" @@ to_tsquery('simple',:fts) THEN 2000
                 WHEN d."AliasVector" @@ to_tsquery('simple',:fts) THEN 2500
                 WHEN d."Vector" @@ to_tsquery('simple',:fts) THEN 1000
                 WHEN :query <% d."NormalizedTitle" THEN 500 ELSE 0 END
              + 100 * ts_rank_cd(d."Vector",to_tsquery('simple',:fts),32)
              + 50 * word_similarity(:query,d."NormalizedTitle") AS score
          FROM "SearchDocument" d JOIN "Space" s ON s."Id"=d."SpaceId"
            JOIN "Page" p ON p."Id"=d."PageId" CROSS JOIN actor a
          WHERE NOT p."IsTrashed" AND p."PublishedRevisionId"=d."RevisionId"
            AND (:space IS NULL OR d."SpaceId"=:space)
            AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access))
            AND (d."Vector" @@ to_tsquery('simple',:fts) OR d."AliasVector" @@ to_tsquery('simple',:fts) OR (NOT :phrase AND d."NormalizedTitle" LIKE :prefix)
              OR (:fuzzy AND (:query <% d."NormalizedTitle"
                OR d."PageId" IN (SELECT "PageId" FROM body))))
            AND (:changed_since IS NULL OR d."PublishedAt">=:changed_since)
          ORDER BY CASE WHEN :latest THEN d."PublishedAt" END DESC NULLS LAST,score DESC,d."Title",d."PageId" LIMIT {{take}} OFFSET {{skip}}
        )
        SELECT r."PageId" AS page_id,r."SpaceId" AS space_id,r."SpaceName" AS space_name,r."Title" AS title,
          left(ts_headline('simple',substr(d."Body",greatest(coalesce(nullif(strpos(d."NormalizedBody",:query),0),strpos(d."NormalizedBody",split_part(:query,' ',1)))-10000,1),20000),
            to_tsquery('simple',:fts),
            'StartSel="", StopSel="", MaxWords=35, MinWords=15, MaxFragments=1'),2000) AS snippet,r."PublishedAt" AS changed_at,
          h.heading->>'title' AS heading_title,h.heading->>'headingId' AS heading_id,matched.alias AS matched_alias,CAST(r.score AS real) AS score
        FROM ranked r JOIN "SearchDocument" d ON d."PageId"=r."PageId"
        LEFT JOIN LATERAL (
          SELECT heading FROM jsonb_array_elements(d."Headings") AS heading
          WHERE heading->>'headingId' IS NOT NULL
            AND (((:phrase OR d."HeadingVector" @@ to_tsquery('simple',:fts))
                AND {{SearchSqlText.Lexemes("heading->>'title'", "heading->>'normalizedTitle'")}} @@ to_tsquery('simple',:fts))
              OR (:fuzzy AND :query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=:query THEN 2 ELSE 1 END DESC,
            word_similarity(:query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE (:phrase OR d."AliasVector" @@ to_tsquery('simple',:fts))
            AND {{SearchSqlText.Lexemes("alias", SearchSqlText.Normalize("alias"))}} @@ to_tsquery('simple',:fts)
          ORDER BY alias LIMIT 1
        ) matched ON true
        ORDER BY CASE WHEN :latest THEN r."PublishedAt" END DESC NULLS LAST,r.score DESC,r."Title",r."PageId"
        """;

    /// <summary>The working revisions the actor may read as drafts (atlas_search_drafts); <paramref name="take"/> and
    /// <paramref name="skip"/> as in <see cref="PublishedFiltered"/>.</summary>
    public static string Drafts(string take = ":take", string skip = ":skip") => $$"""
        WITH {{AccessSql.ActorCte}}, access AS MATERIALIZED (
          SELECT e."PageId",e."Role" FROM "EffectivePageAccess" e
          WHERE e."UserId"=:actor AND e."Role">=1 AND (:space IS NULL OR e."SpaceId"=:space)
        ), accessible AS MATERIALIZED (
          SELECT d."PageId",d."RevisionId",d."NormalizedTitle",d."Vector",d."HeadingVector",d."ChangedAt",
            p."SpaceId",CAST(s."Name" AS text) AS "SpaceName",CAST(r."Title" AS text) AS "Title"
          FROM "WorkingSearchDocument" d JOIN "Page" p ON p."Id"=d."PageId"
            JOIN "PageRevision" r ON r."PageId"=d."PageId" AND r."Id"=d."RevisionId" AND r."ActiveTo" IS NULL
            JOIN "Space" s ON s."Id"=p."SpaceId" CROSS JOIN actor a
          WHERE NOT p."IsTrashed" AND r."Id" IS DISTINCT FROM p."PublishedRevisionId"
            AND (:space IS NULL OR p."SpaceId"=:space)
            AND (:changed_since IS NULL OR d."ChangedAt">=:changed_since)
            AND (p."PublishedRevisionId" IS NOT NULL OR p."OwnerId"=:actor OR a."IsWikiAdmin")
            AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access
              WHERE "Role">=2 OR coalesce(p."PublicationMode",s."PublicationMode")='Live'))
        ), body_probe AS MATERIALIZED (
          -- The body of each real draft, read through its key: a join would let the planner test every working revision.
          SELECT d."PageId" FROM accessible d
          WHERE :fuzzy AND NOT (d."Vector" @@ to_tsquery('simple',:fts)
            OR (NOT :phrase AND d."NormalizedTitle" LIKE :prefix))
            AND (SELECT :query <% body."NormalizedBody" FROM "WorkingSearchDocument" body WHERE body."PageId"=d."PageId")
          LIMIT 64
        ), matches AS MATERIALIZED (
          SELECT "PageId" FROM "WorkingSearchDocument" WHERE "Vector" @@ to_tsquery('simple',:fts)
          UNION
          SELECT "PageId" FROM "WorkingSearchDocument" WHERE NOT :phrase AND "NormalizedTitle" LIKE :prefix
          UNION
          SELECT "PageId" FROM "WorkingSearchDocument"
          WHERE :fuzzy AND NOT ("Vector" @@ to_tsquery('simple', :fts)
            OR (NOT :phrase AND "NormalizedTitle" LIKE :prefix)) AND :query <% "NormalizedTitle"
          UNION
          SELECT "PageId" FROM body_probe WHERE (SELECT count(*) FROM body_probe)<64
          UNION (
            SELECT ordered."PageId" FROM (
              SELECT d."PageId",d."ChangedAt",d."Title",500+50*word_similarity(:query,d."NormalizedTitle") AS score
              FROM accessible d
              WHERE (SELECT count(*) FROM body_probe)=64 AND :fuzzy
                AND NOT (d."Vector" @@ to_tsquery('simple',:fts)
                  OR (NOT :phrase AND d."NormalizedTitle" LIKE :prefix))
              ORDER BY CASE WHEN :latest THEN d."ChangedAt" END DESC NULLS LAST,score DESC,d."Title",d."PageId"
              OFFSET 0
            ) ordered
            WHERE (SELECT :query <% body."NormalizedBody" FROM "WorkingSearchDocument" body WHERE body."PageId"=ordered."PageId")
            ORDER BY CASE WHEN :latest THEN ordered."ChangedAt" END DESC NULLS LAST,ordered.score DESC,ordered."Title",ordered."PageId"
            LIMIT ({{skip}}+{{take}})
          )
        ), ranked AS MATERIALIZED (
          SELECT d."PageId",d."RevisionId",d."SpaceId",d."SpaceName",d."Title",d."ChangedAt",
            CASE WHEN d."NormalizedTitle"=:query THEN 10000 WHEN d."NormalizedTitle" LIKE :prefix THEN 5000
                 WHEN ts_filter(d."Vector",'{a}') @@ to_tsquery('simple',:fts) THEN 3000
                 WHEN d."HeadingVector" @@ to_tsquery('simple',:fts) THEN 2000
                 WHEN d."Vector" @@ to_tsquery('simple',:fts) THEN 1000 ELSE 500 END
              + 100 * ts_rank_cd(d."Vector",to_tsquery('simple',:fts),32)
              + 50 * word_similarity(:query,d."NormalizedTitle") AS score
          FROM matches match JOIN accessible d ON d."PageId"=match."PageId"
          ORDER BY CASE WHEN :latest THEN d."ChangedAt" END DESC NULLS LAST,score DESC,d."Title",d."PageId"
          LIMIT {{take}} OFFSET {{skip}}
        )
        SELECT r."PageId" AS page_id,r."SpaceId" AS space_id,r."SpaceName" AS space_name,r."Title" AS title,
          left(ts_headline('simple',d."Markdown",to_tsquery('simple',:fts),
            'StartSel="", StopSel="", MaxWords=35, MinWords=15, MaxFragments=1'),2000) AS snippet,r."ChangedAt" AS changed_at,
          h.heading->>'title' AS heading_title,h.heading->>'headingId' AS heading_id,CAST(NULL AS text) AS matched_alias,CAST(r.score AS real) AS score
        FROM ranked r JOIN "PageRevision" d ON d."Id"=r."RevisionId"
          JOIN "WorkingSearchDocument" w ON w."PageId"=r."PageId" AND w."RevisionId"=r."RevisionId"
        LEFT JOIN LATERAL (
          SELECT heading FROM jsonb_array_elements(w."Headings") AS heading
          WHERE heading->>'headingId' IS NOT NULL
            AND (((:phrase OR w."HeadingVector" @@ to_tsquery('simple',:fts))
                AND {{SearchSqlText.Lexemes("heading->>'title'", "heading->>'normalizedTitle'")}} @@ to_tsquery('simple',:fts))
              OR (:fuzzy AND :query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=:query THEN 2 ELSE 1 END DESC,
            word_similarity(:query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        ORDER BY CASE WHEN :latest THEN r."ChangedAt" END DESC NULLS LAST,r.score DESC,r."Title",r."PageId"
        """;

    /// <summary>The unified scope: the published hits, then the drafts of pages without one, in one order (atlas_search_all).</summary>
    public static readonly string All = $$"""
        WITH published AS MATERIALIZED (
          SELECT * FROM ({{PublishedFiltered("(:skip+:take)", "0")}}) published_hits
        ), draft AS MATERIALIZED (
          SELECT * FROM ({{Drafts("(:skip+:take)", "0")}}) draft_hits
        ), merged AS (
          SELECT p.*,false AS is_draft FROM published p
          UNION ALL
          SELECT d.*,true AS is_draft FROM draft d
          WHERE NOT EXISTS (SELECT 1 FROM published p WHERE p.page_id=d.page_id)
        )
        SELECT page_id,space_id,space_name,title,snippet,changed_at,heading_title,heading_id,matched_alias,is_draft
        FROM merged
        ORDER BY CASE WHEN :latest THEN changed_at END DESC NULLS LAST,score DESC,title,page_id
        LIMIT :take OFFSET :skip
        """;

    /// <summary>The spaces whose name matches, at most ten (atlas_search_spaces).</summary>
    public static readonly string Spaces = $$"""
        WITH {{AccessSql.ActorCte}}, visible AS MATERIALIZED (
          SELECT s.*,{{SearchSqlText.Normalize("s.\"Name\"")}} AS normalized FROM "Space" s CROSS JOIN actor a
          WHERE s."TrashedAt" IS NULL AND (:space IS NULL OR s."Id"=:space) AND (a."IsWikiAdmin" OR EXISTS (
            SELECT 1 FROM "EffectiveSpaceGrant" g WHERE g."SpaceId"=s."Id"
              AND (g."UserId"=:actor OR EXISTS (
                SELECT 1 FROM "EffectiveGroupMember" m WHERE m."GroupId"=g."GroupId" AND m."UserId"=:actor AND m."ActiveTo" IS NULL)))
            -- D8: a space in which the account can list a page shows its name, icon and colour.
            OR EXISTS (
              SELECT 1 FROM "EffectivePageAccess" e JOIN "Page" p ON p."Id"=e."PageId"
              WHERE e."UserId"=:actor AND e."SpaceId"=s."Id" AND NOT p."IsTrashed"
                AND (p."PublishedRevisionId" IS NOT NULL OR (e."Role">=2 AND p."OwnerId"=:actor))))
        )
        SELECT "Id" AS space_id,CAST("Name" AS text) AS space_name,CAST("Icon" AS text) AS space_icon,CAST("Color" AS text) AS space_color FROM visible
        WHERE (NOT :phrase AND normalized LIKE :prefix) OR to_tsvector('simple',normalized) @@ to_tsquery('simple',:fts)
          OR (:fuzzy AND :query <% normalized)
        ORDER BY CASE WHEN normalized=:query THEN 2 WHEN normalized LIKE :prefix THEN 1 ELSE 0 END DESC,
          word_similarity(:query,normalized) DESC,"Name","Id" LIMIT 10
        """;

    /// <summary>S3: one row, at most one readable published title or alias close to the query, else null (atlas_search_did_you_mean).</summary>
    public static readonly string DidYouMean = $$"""
        SELECT (
          WITH {{AccessSql.ActorCte}}, access AS MATERIALIZED (
            SELECT e."PageId" FROM "EffectivePageAccess" e
            WHERE e."UserId"=:actor AND (:space IS NULL OR e."SpaceId"=:space)
          ), named AS MATERIALIZED (
            SELECT d."PageId",d."RevisionId",CAST(d."Title" AS text) AS suggestion,d."NormalizedTitle" AS normalized
            FROM "SearchDocument" d WHERE d."NormalizedTitle" % :query
            UNION ALL
            SELECT d."PageId",d."RevisionId",alias,{{SearchSqlText.Normalize("alias")}}
            FROM "SearchDocument" d CROSS JOIN LATERAL jsonb_array_elements_text(d."Aliases") AS alias
            WHERE d."Aliases"<>CAST('[]' AS jsonb)
          ), readable AS MATERIALIZED (
            SELECT n.suggestion,n.normalized,similarity(n.normalized,:query) AS similarity
            FROM named n JOIN "Page" p ON p."Id"=n."PageId" AND p."PublishedRevisionId"=n."RevisionId" CROSS JOIN actor a
            WHERE NOT p."IsTrashed" AND (:space IS NULL OR p."SpaceId"=:space)
              AND (a."IsWikiAdmin" OR n."PageId" IN (SELECT "PageId" FROM access))
          )
          SELECT r.suggestion FROM readable r
          WHERE r.similarity>=0.45 AND r.normalized<>:query
            AND NOT (string_to_array(:query,' ') <@ string_to_array(r.normalized,' '))
            AND NOT EXISTS (SELECT 1 FROM readable exact WHERE exact.normalized=:query)
          ORDER BY r.similarity DESC,r.suggestion
          LIMIT 1) AS suggestion
        """;

    /// <summary>S1/S2: today's lexical ranking beside the nearest chunks of the active generation (atlas_search_hybrid_candidates);
    /// <paramref name="lexicalTake"/> is how many lexical rows to rank; the other arguments replace their parameters (the answer sources pass
    /// expressions).</summary>
    public static string HybridCandidates(string lexicalTake = ":lexical_take", string fuzzy = ":fuzzy", string changedSince = ":changed_since",
        string latest = ":latest", string generation = ":generation") => $$"""
        WITH {{AccessSql.ActorCte}}, access AS MATERIALIZED (
          SELECT e."PageId" FROM "EffectivePageAccess" e
          WHERE e."UserId"=:actor AND (:space IS NULL OR e."SpaceId"=:space)
        ), strict AS MATERIALIZED (
          SELECT count(*) AS hits FROM (
            SELECT 1 FROM "SearchDocument" d JOIN "Page" p ON p."Id"=d."PageId" CROSS JOIN actor a
            WHERE {{fuzzy}} AND NOT {{latest}} AND NOT p."IsTrashed" AND p."PublishedRevisionId"=d."RevisionId"
              AND (:space IS NULL OR d."SpaceId"=:space)
              AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access))
              AND (d."Vector" @@ to_tsquery('simple',:fts) OR d."AliasVector" @@ to_tsquery('simple',:fts) OR (NOT :phrase AND d."NormalizedTitle" LIKE :prefix)
                OR :query <% d."NormalizedTitle")
              AND ({{changedSince}} IS NULL OR d."PublishedAt">={{changedSince}})
            LIMIT {{lexicalTake}}) found
        ), body AS MATERIALIZED (
          SELECT d."PageId" FROM "SearchDocument" d
          WHERE {{fuzzy}} AND ({{latest}} OR (SELECT hits FROM strict)<{{lexicalTake}}) AND :query <% left(d."NormalizedBody",20000)
        ), lexical AS MATERIALIZED (
          -- Today's published ranking (migration 2026100102), its rows numbered in today's order.
          SELECT ranked.*, CAST((row_number() OVER ()) AS integer) AS lexical_rank FROM (
            SELECT d."PageId",d."SpaceId",d."Title",d."PublishedAt",CAST(s."Name" AS text) AS "SpaceName",
              CASE WHEN d."NormalizedTitle"=:query THEN 10000
                   WHEN d."NormalizedTitle" LIKE :prefix THEN 5000
                   WHEN ts_filter(d."Vector",'{a}') @@ to_tsquery('simple',:fts) THEN 3000
                   WHEN d."HeadingVector" @@ to_tsquery('simple',:fts) THEN 2000
                   WHEN d."AliasVector" @@ to_tsquery('simple',:fts) THEN 2500
                   WHEN d."Vector" @@ to_tsquery('simple',:fts) THEN 1000
                   WHEN :query <% d."NormalizedTitle" THEN 500 ELSE 0 END
                + 100 * ts_rank_cd(d."Vector",to_tsquery('simple',:fts),32)
                + 50 * word_similarity(:query,d."NormalizedTitle") AS score
            FROM "SearchDocument" d JOIN "Space" s ON s."Id"=d."SpaceId"
              JOIN "Page" p ON p."Id"=d."PageId" CROSS JOIN actor a
            WHERE NOT p."IsTrashed" AND p."PublishedRevisionId"=d."RevisionId"
              AND (:space IS NULL OR d."SpaceId"=:space)
              AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access))
              AND (d."Vector" @@ to_tsquery('simple',:fts) OR d."AliasVector" @@ to_tsquery('simple',:fts) OR (NOT :phrase AND d."NormalizedTitle" LIKE :prefix)
                OR ({{fuzzy}} AND (:query <% d."NormalizedTitle"
                  OR d."PageId" IN (SELECT "PageId" FROM body))))
              AND ({{changedSince}} IS NULL OR d."PublishedAt">={{changedSince}})
            ORDER BY CASE WHEN {{latest}} THEN d."PublishedAt" END DESC NULLS LAST,score DESC,d."Title",d."PageId"
            LIMIT {{lexicalTake}}
          ) ranked
        ), generation AS MATERIALIZED (
          -- One snapshot of the pair (spec RAG §6): the generation the query was embedded for, only while it is Active.
          SELECT g."Id" FROM "IndexGeneration" g WHERE g."Id"={{generation}} AND g."State"='Active'
        ), included AS MATERIALIZED (
          SELECT s."Id" FROM "Space" s WHERE (:space IS NULL OR s."Id"=:space) AND {{AccessSql.SpaceInAi("s.\"Id\"")}}
        ), nearest AS MATERIALIZED (
          -- Tunable: 200 vector candidates. Every filter is evaluated on each scanned chunk (HNSW iterative scan). They are
          -- correlated checks of the chunk's row, not joins: a join lets the planner start from the pages (it badly
          -- underestimates the chunk-to-published-revision join) and compute the distance of every chunk instead of walking
          -- the graph (measured 150 ms against 3 ms over 25 000 chunks).
          SELECT c."Id" AS chunk_id,c."PageId",c."Embedding" <=> CAST(:embedding AS vector) AS distance
          FROM "KnowledgeChunk" c
          WHERE c."GenerationId"={{generation}} AND EXISTS (SELECT 1 FROM generation) AND EXISTS (SELECT 1 FROM actor)
            AND strpos(c."Text",E'\n')>0
            AND (SELECT p."PublishedRevisionId"=c."RevisionId" AND NOT p."IsTrashed" AND NOT p."IsTemplate"
                   AND p."SpaceId" = ANY (ARRAY(SELECT "Id" FROM included))
                   AND ({{changedSince}} IS NULL OR d."PublishedAt">={{changedSince}})
                 FROM "Page" p JOIN "SearchDocument" d ON d."PageId"=p."Id" AND d."RevisionId"=p."PublishedRevisionId"
                 WHERE p."Id"=c."PageId")
            AND ((SELECT a."IsWikiAdmin" FROM actor a) OR c."PageId" IN (SELECT "PageId" FROM access))
          ORDER BY c."Embedding" <=> CAST(:embedding AS vector)
          LIMIT 200
        ), vec AS MATERIALIZED (
          -- relaxed_order may return the scan slightly out of order: rank again by the exact distance.
          SELECT n.chunk_id,n."PageId",n.distance,CAST((row_number() OVER (ORDER BY n.distance,n.chunk_id)) AS integer) AS chunk_rank
          FROM nearest n WHERE 1 - n.distance >= :min_similarity
        ), vec_pages AS MATERIALIZED (
          SELECT best.*,CAST((row_number() OVER (ORDER BY best.distance,best."PageId")) AS integer) AS vector_rank
          FROM (SELECT DISTINCT ON (v."PageId") v."PageId",v.chunk_id,v.chunk_rank,v.distance FROM vec v ORDER BY v."PageId",v.distance,v.chunk_id) best
        )
        SELECT COALESCE(l."PageId",v."PageId") AS page_id,COALESCE(l."SpaceId",d."SpaceId") AS space_id,
          COALESCE(l."SpaceName",CAST(s."Name" AS text)) AS space_name,CAST(COALESCE(l."Title",d."Title") AS text) AS title,
          COALESCE(l."PublishedAt",d."PublishedAt") AS changed_at,CAST(l.score AS real) AS lexical_score,l.lexical_rank,v.vector_rank,
          v.chunk_id,v.chunk_rank,l."PageId" IS NULL AS semantic
        FROM lexical l
        FULL JOIN vec_pages v ON v."PageId"=l."PageId"
        LEFT JOIN "SearchDocument" d ON l."PageId" IS NULL AND d."PageId"=v."PageId"
        LEFT JOIN "Space" s ON l."PageId" IS NULL AND s."Id"=d."SpaceId"
        """;

    /// <summary>S1/S2: the fused lexical, vector and (with <paramref name="drafts"/>) draft order (atlas_search_hybrid).</summary>
    public static string Hybrid(bool drafts) => $$"""
        WITH candidate AS MATERIALIZED (
          -- Tunable: the hybrid window of 300 needs the lexical top 300; a later page needs the lexical rows up to it.
          SELECT * FROM ({{HybridCandidates("greatest(300,:skip+:take)")}}) candidates
        ), draft AS MATERIALIZED (
          -- Drafts are ordered among themselves by their own rank, so the first skip+take of them are all a page can show.
          SELECT d.*,CAST((row_number() OVER ()) AS integer) AS draft_rank
          FROM ({{Drafts("(:skip+:take)", "0")}}) d
          WHERE {{(drafts ? "true" : "false")}}
        ), entry AS MATERIALIZED (
          -- tier: today's title tiers (tunable boundary 1500); fused: weights 1 (vector), 0.5 (lexical), 1.5 (draft), k = 60.
          SELECT c.page_id,c.space_id,c.space_name,c.title,c.changed_at,c.lexical_score AS score,false AS is_draft,c.semantic,
            c.chunk_id,c.chunk_rank,CAST(NULL AS text) AS snippet,CAST(NULL AS text) AS heading_title,CAST(NULL AS text) AS heading_id,
            CASE WHEN c.lexical_score>=1500 THEN c.lexical_score END AS tier,
            COALESCE(CAST(1.0 AS float8)/(60+c.vector_rank),0)+COALESCE(CAST(0.5 AS float8)/(60+c.lexical_rank),0) AS fused,
            (c.lexical_rank<=300 OR c.vector_rank IS NOT NULL) AS in_window
          FROM candidate c
          UNION ALL
          SELECT d.page_id,d.space_id,d.space_name,d.title,d.changed_at,d.score,true,false,
            CAST(NULL AS bigint),CAST(NULL AS integer),d.snippet,d.heading_title,d.heading_id,
            CASE WHEN d.score>=1500 THEN d.score END,
            CAST(1.5 AS float8)/(60+d.draft_rank),
            d.draft_rank<=300
          FROM draft d
        ), best AS MATERIALIZED (
          SELECT DISTINCT ON (e.page_id) e.* FROM entry e
          ORDER BY e.page_id,CASE WHEN :latest THEN e.changed_at END DESC NULLS LAST,e.tier DESC NULLS LAST,e.fused DESC,e.is_draft
        ), hybrid AS MATERIALIZED (
          SELECT b.page_id,CAST((row_number() OVER (ORDER BY CASE WHEN :latest THEN b.changed_at END DESC NULLS LAST,
              b.tier DESC NULLS LAST,b.fused DESC,b.title,b.page_id)) AS integer) AS position
          FROM best b WHERE b.in_window
          ORDER BY position LIMIT 300
        ), positioned AS MATERIALIZED (
          SELECT * FROM hybrid
          UNION ALL
          -- After the window: today's lexical order (the unified scope's merge order), never a page shown above.
          SELECT b.page_id,CAST(((SELECT count(*) FROM hybrid)+row_number() OVER (ORDER BY CASE WHEN :latest THEN b.changed_at END DESC NULLS LAST,
              b.score DESC,b.title,b.page_id)) AS integer)
          FROM best b WHERE b.score IS NOT NULL AND NOT EXISTS (SELECT 1 FROM hybrid h WHERE h.page_id=b.page_id)
        ), page AS MATERIALIZED (
          SELECT b.*,x.position FROM positioned x JOIN best b ON b.page_id=x.page_id
          WHERE x.position>:skip ORDER BY x.position LIMIT :take
        )
        SELECT r.page_id,r.space_id,r.space_name,r.title,
          CASE WHEN r.is_draft THEN r.snippet
               WHEN r.semantic THEN left(d."Body",2000)
               ELSE left(ts_headline('simple',d."Body",to_tsquery('simple',:fts),
                 'StartSel="", StopSel="", MaxWords=35, MinWords=15, MaxFragments=1'),2000) END AS snippet,
          r.changed_at,
          CASE WHEN r.is_draft THEN r.heading_title WHEN r.semantic THEN CAST(k."HeadingTitle" AS text) ELSE h.heading->>'title' END AS heading_title,
          CASE WHEN r.is_draft THEN r.heading_id WHEN r.semantic THEN CAST(k."HeadingId" AS text) ELSE h.heading->>'headingId' END AS heading_id,
          matched.alias AS matched_alias,CAST(COALESCE(r.score,0) AS real) AS score,r.is_draft,r.semantic,
          -- The passage: the page's best chunk, for a lexical page only when it is among the 20 nearest (tunable) and leads
          -- into a section. Its body without the context line "{title} › {heading}".
          CASE WHEN k."Id" IS NULL THEN NULL WHEN strpos(k."Text",E'\n')>0 THEN substr(k."Text",strpos(k."Text",E'\n')+1) ELSE '' END AS passage,
          CAST(k."HeadingTitle" AS text) AS passage_heading_title,CAST(k."HeadingId" AS text) AS passage_heading_id
        FROM page r
        LEFT JOIN "SearchDocument" d ON NOT r.is_draft AND d."PageId"=r.page_id
        LEFT JOIN "KnowledgeChunk" k ON k."Id"=r.chunk_id AND (r.semantic OR (r.chunk_rank<=20 AND k."HeadingId" IS NOT NULL))
        LEFT JOIN LATERAL (
          SELECT heading FROM jsonb_array_elements(d."Headings") AS heading
          WHERE NOT r.is_draft AND NOT r.semantic AND heading->>'headingId' IS NOT NULL
            AND ({{SearchSqlText.Lexemes("heading->>'title'", "heading->>'normalizedTitle'")}} @@ to_tsquery('simple',:fts)
              OR (:fuzzy AND :query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=:query THEN 2 ELSE 1 END DESC,
            word_similarity(:query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE NOT r.is_draft AND NOT r.semantic
            AND {{SearchSqlText.Lexemes("alias", SearchSqlText.Normalize("alias"))}} @@ to_tsquery('simple',:fts)
          ORDER BY alias LIMIT 1
        ) matched ON true
        ORDER BY r.position
        """;

    /// <summary>S1/S2 in the unified scope (atlas_search_hybrid_all).</summary>
    public static readonly string HybridAll = $$"""
        SELECT page_id,space_id,space_name,title,snippet,changed_at,heading_title,heading_id,matched_alias,
          is_draft,semantic,passage,passage_heading_title,passage_heading_id
        FROM ({{Hybrid(true)}}) hybrid_hits
        """;

    /// <summary>S1/S2 in the published scope (atlas_search_hybrid_published).</summary>
    public static readonly string HybridPublished = $$"""
        SELECT page_id,space_id,space_name,title,snippet,changed_at,heading_title,heading_id,matched_alias,score,
          semantic,passage,passage_heading_title,passage_heading_id
        FROM ({{Hybrid(false)}}) hybrid_hits
        """;

    /// <summary>P1: readable pages whose title, an alias or an anchored heading is like the typed title (atlas_similar_pages).</summary>
    public static readonly string Similar = $$"""
        WITH {{AccessSql.ActorCte}}, access AS MATERIALIZED (
          SELECT e."PageId",e."Role" FROM "EffectivePageAccess" e WHERE e."UserId"=:actor AND e."Role">=1
        ), candidates AS MATERIALIZED (
          SELECT d."PageId",d."SpaceId",d."Title",false AS "IsDraft",d."NormalizedTitle",d."Aliases",d."Headings"
          FROM "SearchDocument" d JOIN "Page" p ON p."Id"=d."PageId" CROSS JOIN actor a
          WHERE NOT p."IsTrashed" AND NOT p."IsTemplate" AND p."PublishedRevisionId"=d."RevisionId"
            AND d."PageId" IS DISTINCT FROM :exclude
            AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access))
            -- to_tsquery is strict: NULLIF keeps an empty word list from reaching it (no notice, no match).
            AND (d."NormalizedTitle" % :query
              OR d."AliasVector" @@ to_tsquery('simple',NULLIF(:words,''))
              OR (d."Vector" @@ to_tsquery('simple',NULLIF(:words,''))
                AND d."HeadingVector" @@ to_tsquery('simple',NULLIF(:words,''))))
          UNION ALL
          SELECT d."PageId",p."SpaceId",CAST(r."Title" AS text),true,d."NormalizedTitle",CAST('[]' AS jsonb),d."Headings"
          FROM "WorkingSearchDocument" d JOIN "Page" p ON p."Id"=d."PageId"
            JOIN "PageRevision" r ON r."PageId"=d."PageId" AND r."Id"=d."RevisionId" AND r."ActiveTo" IS NULL
            CROSS JOIN actor a
          WHERE p."PublishedRevisionId" IS NULL AND p."OwnerId"=:actor AND NOT p."IsTrashed" AND NOT p."IsTemplate"
            AND d."PageId" IS DISTINCT FROM :exclude
            AND (a."IsWikiAdmin" OR d."PageId" IN (SELECT "PageId" FROM access WHERE "Role">=2))
            AND (d."NormalizedTitle" % :query
              OR (d."Vector" @@ to_tsquery('simple',NULLIF(:words,''))
                AND d."HeadingVector" @@ to_tsquery('simple',NULLIF(:words,''))))
        ), scored AS (
          SELECT c."PageId",c."SpaceId",c."Title",c."IsDraft",
            similarity(c."NormalizedTitle",:query) AS title_score,
            COALESCE((SELECT max(similarity({{SearchSqlText.Normalize("alias")}},:query))
              FROM jsonb_array_elements_text(c."Aliases") AS alias),0) AS alias_score,
            h.heading_title,h.heading_id,COALESCE(h.heading_score,0) AS heading_score
          FROM candidates c
          LEFT JOIN LATERAL (
            SELECT heading->>'title' AS heading_title,heading->>'headingId' AS heading_id,
              CASE WHEN similarity(heading->>'normalizedTitle',:query)>=0.5
                THEN similarity(heading->>'normalizedTitle',:query) END AS heading_score
            FROM jsonb_array_elements(c."Headings") WITH ORDINALITY AS section(heading,position)
            WHERE heading->>'headingId' IS NOT NULL AND length(heading->>'normalizedTitle')>=6
              AND word_similarity(heading->>'normalizedTitle',:query)>=0.8
            ORDER BY similarity(heading->>'normalizedTitle',:query) DESC,
              word_similarity(heading->>'normalizedTitle',:query) DESC,position LIMIT 1
          ) h ON true
        )
        SELECT r."PageId" AS page_id,r."SpaceId" AS space_id,CAST(s."Name" AS text) AS space_name,r."Title" AS title,r."IsDraft" AS is_draft,
          CASE WHEN r.title_score>=r.alias_score AND r.title_score>=r.heading_score THEN 'title'
               WHEN r.alias_score>=r.heading_score THEN 'alias' ELSE 'heading' END AS matched_by,
          r.heading_title,r.heading_id,CAST(greatest(r.title_score,r.alias_score,r.heading_score) AS real) AS score
        FROM scored r JOIN "Space" s ON s."Id"=r."SpaceId"
        WHERE greatest(r.title_score,r.alias_score,r.heading_score)>=0.35
        ORDER BY score DESC,CASE WHEN r."SpaceId"=:space THEN 0 ELSE 1 END,r."Title",r."PageId"
        LIMIT :take
        """;

    /// <summary>P1, vector part (V5): readable published pages whose title or content is near the title's vector
    /// (atlas_similar_pages_semantic).</summary>
    public static readonly string SimilarSemantic = $$"""
        WITH {{AccessSql.ActorCte}}, access AS MATERIALIZED (
          SELECT e."PageId" FROM "EffectivePageAccess" e WHERE e."UserId"=:actor AND e."Role">=1
        ), generation AS MATERIALIZED (
          SELECT g."Id" FROM "IndexGeneration" g WHERE g."Id"=:generation AND g."State"='Active'
        ), included AS MATERIALIZED (
          SELECT s."Id" FROM "Space" s WHERE {{AccessSql.SpaceInAi("s.\"Id\"")}}
        ), by_title AS MATERIALIZED (
          -- Tunable: 40 candidates from each index. Correlated filters on the scanned row, as in hybrid search.
          SELECT k."PageId",k."TitleEmbedding" <=> CAST(:vector AS vector) AS distance
          FROM "KnowledgePage" k
          WHERE k."GenerationId"=:generation AND NOT k."IsTemplate" AND EXISTS (SELECT 1 FROM generation) AND EXISTS (SELECT 1 FROM actor)
            AND k."PageId" IS DISTINCT FROM :exclude
            AND (SELECT p."PublishedRevisionId"=k."RevisionId" AND NOT p."IsTrashed" AND NOT p."IsTemplate"
                   AND p."SpaceId" = ANY (ARRAY(SELECT "Id" FROM included))
                 FROM "Page" p WHERE p."Id"=k."PageId")
            AND ((SELECT a."IsWikiAdmin" FROM actor a) OR k."PageId" IN (SELECT "PageId" FROM access))
          ORDER BY k."TitleEmbedding" <=> CAST(:vector AS vector)
          LIMIT 40
        ), by_content AS MATERIALIZED (
          SELECT k."PageId",k."Centroid" <=> CAST(:vector AS vector) AS distance
          FROM "KnowledgePage" k
          WHERE k."GenerationId"=:generation AND k."Centroid" IS NOT NULL AND NOT k."IsTemplate"
            AND EXISTS (SELECT 1 FROM generation) AND EXISTS (SELECT 1 FROM actor)
            AND k."PageId" IS DISTINCT FROM :exclude
            AND (SELECT p."PublishedRevisionId"=k."RevisionId" AND NOT p."IsTrashed" AND NOT p."IsTemplate"
                   AND p."SpaceId" = ANY (ARRAY(SELECT "Id" FROM included))
                 FROM "Page" p WHERE p."Id"=k."PageId")
            AND ((SELECT a."IsWikiAdmin" FROM actor a) OR k."PageId" IN (SELECT "PageId" FROM access))
          ORDER BY k."Centroid" <=> CAST(:vector AS vector)
          LIMIT 40
        ), scored AS (
          SELECT x."PageId",max(1-x.distance) AS score
          FROM (SELECT * FROM by_title UNION ALL SELECT * FROM by_content) x
          GROUP BY x."PageId"
        )
        SELECT r."PageId" AS page_id,p."SpaceId" AS space_id,CAST(s."Name" AS text) AS space_name,CAST(d."Title" AS text) AS title,
          CAST(r.score AS real) AS score
        FROM scored r JOIN "Page" p ON p."Id"=r."PageId"
          JOIN "SearchDocument" d ON d."PageId"=p."Id" AND d."RevisionId"=p."PublishedRevisionId"
          JOIN "Space" s ON s."Id"=p."SpaceId"
        WHERE r.score>=:min_similarity
        ORDER BY r.score DESC,CASE WHEN p."SpaceId"=:space THEN 0 ELSE 1 END,d."Title",r."PageId"
        LIMIT :take
        """;
}
