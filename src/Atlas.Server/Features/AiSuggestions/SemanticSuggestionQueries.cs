using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Identity;

namespace Atlas.Server.Features.AiSuggestions;

/// <summary>
/// The SQL of E2, E4, P2 and P3 (Atlas 3.6, no logic in the database): the bodies of atlas_link_candidates,
/// atlas_link_candidates_semantic, atlas_rank_by_context, atlas_placement_parent and atlas_template_suggestion (migrations 2026100570
/// to 2026102010) as query text, with the same parameters (named without <c>p_</c>, vectors and arrays bound as text and cast here)
/// and the same output columns. Access, publication, trash, templates, generation and space inclusion are decided here;
/// <see cref="SemanticSuggestionSql"/> binds and reads. SearchParityTests compares each with the function it replaced.
/// </summary>
public static class SemanticSuggestionQueries
{
    /// <summary>E2: up to three readable published pages per normalized phrase whose title or alias is the phrase. Columns: phrase,
    /// page_id, space_id, space_name, title, matched_by.</summary>
    public static readonly string LinkCandidates = $$"""
        WITH {{AccessSql.ActorCte}}, wanted AS MATERIALIZED (
          SELECT DISTINCT w AS phrase FROM unnest(CAST(:phrases AS text[])) AS w WHERE length(w) > 0
        ), matched AS MATERIALIZED (
          SELECT w.phrase,d."PageId",d."RevisionId",CAST(d."Title" AS text) AS "Title",'title' AS matched_by
          FROM wanted w JOIN "SearchDocument" d ON d."NormalizedTitle"=w.phrase
          UNION ALL
          SELECT w.phrase,d."PageId",d."RevisionId",CAST(d."Title" AS text),'alias'
          FROM "SearchDocument" d CROSS JOIN LATERAL jsonb_array_elements_text(d."Aliases") AS alias
            JOIN wanted w ON w.phrase={{SearchSqlText.Normalize("alias")}}
          WHERE d."Aliases"<>CAST('[]' AS jsonb)
        ), readable AS MATERIALIZED (
          SELECT DISTINCT ON (m.phrase,m."PageId") m.phrase,m."PageId",p."SpaceId",m."Title",m.matched_by
          FROM matched m JOIN "Page" p ON p."Id"=m."PageId" AND p."PublishedRevisionId"=m."RevisionId" CROSS JOIN actor a
          WHERE NOT p."IsTrashed" AND NOT p."IsTemplate" AND m."PageId" IS DISTINCT FROM :exclude
            AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
              WHERE e."UserId"=:actor AND e."PageId"=m."PageId" AND e."Role">=1))
          ORDER BY m.phrase,m."PageId",CASE WHEN m.matched_by='title' THEN 0 ELSE 1 END
        ), ranked AS (
          SELECT r.*,row_number() OVER (PARTITION BY r.phrase ORDER BY CASE WHEN r.matched_by='title' THEN 0 ELSE 1 END,
            CASE WHEN r."SpaceId"=:space THEN 0 ELSE 1 END,r."Title",r."PageId") AS position
          FROM readable r
        )
        SELECT r.phrase,r."PageId" AS page_id,r."SpaceId" AS space_id,CAST(s."Name" AS text) AS space_name,r."Title" AS title,r.matched_by
        FROM ranked r JOIN "Space" s ON s."Id"=r."SpaceId"
        WHERE r.position<=3
        ORDER BY r.phrase,r.position
        """;

    /// <summary>E4: the readable published page nearest each phrase's vector, at <c>:min_similarity</c> or more. Columns: phrase,
    /// page_id, space_id, space_name, title, similarity.</summary>
    public static readonly string LinkCandidatesSemantic = $$"""
        WITH {{AccessSql.ActorCte}}, access AS MATERIALIZED (
          SELECT e."PageId" FROM "EffectivePageAccess" e WHERE e."UserId"=:actor AND e."Role">=1
        ), generation AS MATERIALIZED (
          SELECT g."Id" FROM "IndexGeneration" g WHERE g."Id"=:generation AND g."State"='Active'
        ), included AS MATERIALIZED (
          SELECT s."Id" FROM "Space" s WHERE {{AccessSql.SpaceInAi("s.\"Id\"")}}
        ), wanted AS MATERIALIZED (
          SELECT w.phrase,w.vector FROM unnest(CAST(:phrases AS text[]),CAST(:vectors AS vector[])) AS w(phrase,vector) WHERE w.vector IS NOT NULL
        ), nearest AS MATERIALIZED (
          SELECT w.phrase,n."PageId",n.distance FROM wanted w CROSS JOIN LATERAL (
            -- Tunable, 5 candidates per phrase, relaxed order is re-ranked below.
            SELECT k."PageId",k."TitleEmbedding" <=> w.vector AS distance
            FROM "KnowledgePage" k
            WHERE k."GenerationId"=:generation AND NOT k."IsTemplate" AND EXISTS (SELECT 1 FROM generation) AND EXISTS (SELECT 1 FROM actor)
              AND k."PageId" IS DISTINCT FROM :exclude
              AND (SELECT p."PublishedRevisionId"=k."RevisionId" AND NOT p."IsTrashed" AND NOT p."IsTemplate"
                     AND p."SpaceId" = ANY (ARRAY(SELECT "Id" FROM included))
                   FROM "Page" p WHERE p."Id"=k."PageId")
              AND ((SELECT a."IsWikiAdmin" FROM actor a) OR k."PageId" IN (SELECT "PageId" FROM access))
            ORDER BY k."TitleEmbedding" <=> w.vector
            LIMIT 5
          ) n
        ), best AS (
          SELECT DISTINCT ON (n.phrase) n.phrase,n."PageId",1-n.distance AS similarity
          FROM nearest n WHERE 1-n.distance>=CAST(:min_similarity AS real)
          ORDER BY n.phrase,n.distance,n."PageId"
        )
        SELECT b.phrase,b."PageId" AS page_id,p."SpaceId" AS space_id,CAST(s."Name" AS text) AS space_name,CAST(d."Title" AS text) AS title,
          CAST(b.similarity AS real) AS similarity
        FROM best b JOIN "Page" p ON p."Id"=b."PageId"
          JOIN "SearchDocument" d ON d."PageId"=p."Id" AND d."RevisionId"=p."PublishedRevisionId"
          JOIN "Space" s ON s."Id"=p."SpaceId"
        ORDER BY b.phrase
        """;

    /// <summary>P2: the readable published candidates in spaces taking part in AI, by the similarity of their content to the
    /// context's vector. Columns: page_id, similarity; most similar first.</summary>
    public static readonly string RankByContext = $$"""
        WITH {{AccessSql.ActorCte}}, generation AS MATERIALIZED (
          SELECT g."Id" FROM "IndexGeneration" g WHERE g."Id"=:generation AND g."State"='Active'
        ), candidate AS MATERIALIZED (
          SELECT p."Id",p."SpaceId",COALESCE(k."Centroid",k."TitleEmbedding") AS embedding
          FROM "Page" p
          JOIN "KnowledgePage" k ON k."GenerationId"=:generation AND k."PageId"=p."Id" AND k."RevisionId"=p."PublishedRevisionId"
          CROSS JOIN actor a
          WHERE p."Id"=ANY(CAST(:candidates AS uuid[])) AND EXISTS (SELECT 1 FROM generation) AND NOT p."IsTrashed" AND NOT p."IsTemplate"
            AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
              WHERE e."UserId"=:actor AND e."PageId"=p."Id" AND e."Role">=1))
        ), included AS MATERIALIZED (
          SELECT x."SpaceId" FROM (SELECT DISTINCT "SpaceId" FROM candidate) x WHERE {{AccessSql.SpaceInAi("x.\"SpaceId\"")}}
        )
        SELECT c."Id" AS page_id,CAST(1-(c.embedding <=> CAST(:vector AS vector)) AS real) AS similarity
        FROM candidate c JOIN included i ON i."SpaceId"=c."SpaceId"
        ORDER BY similarity DESC,c."Id"
        """;

    /// <summary>P3: the parent most of the five nearest readable pages of the space share, if the actor may write below it (or the
    /// root, when allowed), with its readable path. Zero rows or one: parent_id, path_ids, path_titles (top down, the parent last).</summary>
    public static readonly string Placement = $$"""
        WITH RECURSIVE {{AccessSql.ActorCte}}, generation AS MATERIALIZED (
          SELECT g."Id" FROM "IndexGeneration" g WHERE g."Id"=:generation AND g."State"='Active'
        ), nearest AS MATERIALIZED (
          SELECT p."Id",p."ParentId",
            greatest(1-(k."TitleEmbedding" <=> CAST(:vector AS vector)),COALESCE(1-(k."Centroid" <=> CAST(:vector AS vector)),0)) AS similarity
          FROM "Page" p
          JOIN "KnowledgePage" k ON k."GenerationId"=:generation AND k."PageId"=p."Id" AND k."RevisionId"=p."PublishedRevisionId"
          CROSS JOIN actor a
          WHERE p."SpaceId"=:space AND NOT p."IsTrashed" AND NOT p."IsTemplate"
            AND EXISTS (SELECT 1 FROM generation) AND (SELECT {{AccessSql.SpaceInAi(":space")}})
            AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
              WHERE e."UserId"=:actor AND e."PageId"=p."Id" AND e."Role">=1))
        ), top AS MATERIALIZED (
          SELECT n."Id",n."ParentId",CAST(row_number() OVER (ORDER BY n.similarity DESC,n."Id") AS integer) AS position
          FROM nearest n WHERE n.similarity>=CAST(:min_similarity AS real)
          ORDER BY n.similarity DESC,n."Id"
          LIMIT 5
        ), candidate AS MATERIALIZED (
          SELECT t."ParentId" AS parent_id,count(*) AS votes,min(t.position) AS best FROM top t GROUP BY t."ParentId"
        ), chain(candidate,id,parent,depth) AS (
          SELECT c.parent_id,p."Id",p."ParentId",0 FROM candidate c JOIN "Page" p ON p."Id"=c.parent_id
          UNION ALL
          SELECT ch.candidate,p."Id",p."ParentId",ch.depth+1 FROM chain ch JOIN "Page" p ON p."Id"=ch.parent WHERE ch.depth<64
        ), step AS MATERIALIZED (
          SELECT ch.candidate,ch.depth,ch.id,CAST(d."Title" AS text) AS title,
            (d."PageId" IS NOT NULL AND NOT p."IsTrashed" AND NOT p."IsTemplate" AND p."SpaceId"=:space
              AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
                WHERE e."UserId"=:actor AND e."PageId"=p."Id" AND e."Role">=1))) AS readable
          FROM chain ch JOIN "Page" p ON p."Id"=ch.id
          LEFT JOIN "SearchDocument" d ON d."PageId"=p."Id" AND d."RevisionId"=p."PublishedRevisionId"
          CROSS JOIN actor a
        ), chosen AS MATERIALIZED (
          SELECT c.parent_id FROM candidate c CROSS JOIN actor a
          WHERE (c.parent_id IS NULL AND :root_allowed)
            OR (c.parent_id IS NOT NULL
              AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e
                WHERE e."UserId"=:actor AND e."PageId"=c.parent_id AND e."Role">=2))
              AND EXISTS (SELECT 1 FROM step s WHERE s.candidate=c.parent_id)
              AND NOT EXISTS (SELECT 1 FROM step s WHERE s.candidate=c.parent_id AND NOT s.readable))
          ORDER BY c.votes DESC,c.best
          LIMIT 1
        )
        SELECT c.parent_id,
          COALESCE((SELECT array_agg(s.id ORDER BY s.depth DESC) FROM step s WHERE s.candidate=c.parent_id),CAST('{}' AS uuid[])) AS path_ids,
          COALESCE((SELECT array_agg(s.title ORDER BY s.depth DESC) FROM step s WHERE s.candidate=c.parent_id),CAST('{}' AS text[])) AS path_titles
        FROM chosen c
        """;

    /// <summary>The usable template of the space a normalized title asks for: by the dictionary of design §7.8, else by meaning at
    /// <c>:min_similarity</c> or more. Zero rows or one: template_id, title, matched_by.</summary>
    public static readonly string Template = $$"""
        WITH {{AccessSql.ActorCte}}, dictionary(stem) AS (
          -- Tunable (design §7.8), runbook, postup, nasazení, kontakty, onboarding as stems of normalized words, so the
          -- Czech forms match too (postupu, nasazeni, kontakt).
          VALUES ('runbook'),('postup'),('nasazen'),('kontakt'),('onboarding')
        ), wanted AS MATERIALIZED (
          SELECT DISTINCT d.stem FROM dictionary d CROSS JOIN unnest(string_to_array(:title,' ')) AS word
          WHERE word LIKE d.stem || '%'
        ), usable AS MATERIALIZED (
          SELECT p."Id",p."SortOrder",p."PublishedRevisionId",
            CAST(CASE WHEN a."IsWikiAdmin" OR e."Role">=2 THEN cur."Title" ELSE pub."Title" END AS text) AS title
          FROM "Page" p CROSS JOIN actor a
          LEFT JOIN "EffectivePageAccess" e ON e."UserId"=:actor AND e."PageId"=p."Id"
          LEFT JOIN "PageRevision" pub ON pub."Id"=p."PublishedRevisionId"
          JOIN "PageRevision" cur ON cur."PageId"=p."Id" AND cur."ActiveTo" IS NULL
          WHERE p."SpaceId"=:space AND p."IsTemplate" AND NOT p."IsTrashed"
            AND ((p."PublishedRevisionId" IS NOT NULL AND (a."IsWikiAdmin" OR e."Role">=1))
              OR (p."PublishedRevisionId" IS NULL AND (a."IsWikiAdmin" OR (e."Role">=2 AND p."OwnerId"=:actor))))
        ), by_dictionary AS (
          SELECT u."Id",u.title,'dictionary' AS matched_by,0 AS tier,CAST(0 AS float8) AS similarity,u."SortOrder"
          FROM usable u
          WHERE EXISTS (SELECT 1 FROM wanted w CROSS JOIN unnest(string_to_array({{SearchSqlText.Normalize("u.title")}},' ')) AS word
            WHERE word LIKE w.stem || '%')
        ), by_meaning AS (
          SELECT u."Id",u.title,'meaning',1,CAST(1-(k."TitleEmbedding" <=> CAST(:vector AS vector)) AS float8),u."SortOrder"
          FROM usable u
          JOIN "KnowledgePage" k ON k."GenerationId"=:generation AND k."PageId"=u."Id" AND k."RevisionId"=u."PublishedRevisionId"
          WHERE CAST(:vector AS vector) IS NOT NULL
            AND EXISTS (SELECT 1 FROM "IndexGeneration" g WHERE g."Id"=:generation AND g."State"='Active')
            AND (SELECT {{AccessSql.SpaceInAi(":space")}})
            AND 1-(k."TitleEmbedding" <=> CAST(:vector AS vector))>=CAST(:min_similarity AS real)
        )
        SELECT x."Id" AS template_id,x.title,x.matched_by
        FROM (SELECT * FROM by_dictionary UNION ALL SELECT * FROM by_meaning) x
        ORDER BY x.tier,x.similarity DESC,x."SortOrder",x.title,x."Id"
        LIMIT 1
        """;
}
