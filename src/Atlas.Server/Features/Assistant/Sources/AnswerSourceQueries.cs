using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Identity;

namespace Atlas.Server.Features.Assistant.Sources;

/// <summary>
/// The SQL of the page source (Atlas 3.6, no logic in the database): the bodies of atlas_answer_sources and
/// atlas_answer_source_status (migrations 2026100740 to 2026102010) as query text, with the same parameters (named without
/// <c>p_</c>, vectors and arrays bound as text and cast here) and the same output columns. <see cref="AnswerSourceSql"/> binds and
/// reads; SearchParityTests compares each with the function it replaced.
/// </summary>
public static class AnswerSourceQueries
{
    /// <summary>
    /// The chunks a question is answered from: the hybrid search's candidate pages (or the one page asked about), readable,
    /// current and in spaces taking part in AI, each with its best passages, a short section with its neighbours. Columns:
    /// chunk_ordinal, generation_id, page_id, revision_id, space_id, space_name, title, heading_id, heading_title, text, rank.
    /// </summary>
    public static readonly string Sources = $$"""
        WITH {{AccessSql.ActorCte}}, mode AS MATERIALIZED (
          -- The generation the text is read from, and whether the vector belongs to it (one snapshot of the pair, spec RAG §6).
          SELECT g."Id" AS generation,(CAST(:embedding AS vector) IS NOT NULL AND g."Id"=:generation) AS semantic
          FROM "IndexGeneration" g WHERE g."State"='Active'
        ), candidate AS MATERIALIZED (
          SELECT c.page_id,c.lexical_score,c.lexical_rank,c.vector_rank
          FROM ({{Search.SearchQueries.HybridCandidates("300", "false", "CAST(NULL AS timestamptz)", "false",
            "(CASE WHEN CAST(:embedding AS vector) IS NULL THEN NULL ELSE :generation END)")}}) c
          WHERE :page IS NULL
          UNION ALL
          SELECT :page,CAST(NULL AS real),CAST(NULL AS integer),CAST(NULL AS integer) WHERE :page IS NOT NULL
        ), eligible AS MATERIALIZED (
          SELECT p."Id",p."SpaceId",p."PublishedRevisionId",CAST(r."Title" AS text) AS title,c.lexical_score,c.lexical_rank,c.vector_rank
          FROM candidate c JOIN "Page" p ON p."Id"=c.page_id
            JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId" CROSS JOIN actor a CROSS JOIN mode m
          WHERE NOT p."IsTrashed" AND NOT p."IsTemplate"
            AND (:space IS NULL OR p."SpaceId"=:space)
            AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e WHERE e."UserId"=:actor AND e."PageId"=p."Id" AND e."Role">=1))
            AND EXISTS (SELECT 1 FROM "KnowledgePage" k WHERE k."GenerationId"=m.generation AND k."PageId"=p."Id" AND k."RevisionId"=p."PublishedRevisionId")
        ), included AS MATERIALIZED (
          SELECT s."Id" FROM (SELECT DISTINCT e."SpaceId" AS "Id" FROM eligible e) s WHERE {{AccessSql.SpaceInAi("s.\"Id\"")}}
        ), page AS MATERIALIZED (
          SELECT e.*,CAST(row_number() OVER (ORDER BY CASE WHEN e.lexical_score>=1500 THEN e.lexical_score END DESC NULLS LAST,
              COALESCE(CAST(1.0 AS float8)/(60+e.vector_rank),0)+COALESCE(CAST(0.5 AS float8)/(60+e.lexical_rank),0) DESC,e.title,e."Id") AS integer) AS position
          FROM eligible e WHERE e."SpaceId" IN (SELECT "Id" FROM included)
          ORDER BY position LIMIT :pages
        ), chunk AS MATERIALIZED (
          SELECT pg.position,pg.lexical_score,pg."Id" AS page_id,pg."SpaceId",pg."PublishedRevisionId" AS revision_id,pg.title,
            k."GenerationId",k."Ordinal",k."HeadingId",k."HeadingTitle",k."Text",
            CASE WHEN m.semantic THEN k."Embedding" <=> CAST(:embedding AS vector) END AS distance,
            CASE WHEN NOT m.semantic THEN ts_rank_cd(to_tsvector('simple',{{SearchSqlText.Normalize(
              "COALESCE(k.\"HeadingTitle\",'')||' '||substr(k.\"Text\",strpos(k.\"Text\",E'\\n')+1)")}}),to_tsquery('simple',:fts)) END AS lexical
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
          WHERE :page IS NULL AND a.body_length<300 AND sz.chunk_count>=4
            AND to_tsvector('simple',{{SearchSqlText.Normalize("pg.title")}}) @@ to_tsquery('simple',:fts)
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
            OR (NOT n.focused AND n.nth<=CASE WHEN :page IS NULL THEN :per_page ELSE :take END
              AND (n.nth=1 OR :page IS NOT NULL OR 1-n.distance>=CAST(:min_similarity AS real) OR n.lexical>0))
        )
        SELECT n."Ordinal" AS chunk_ordinal,n."GenerationId" AS generation_id,n.page_id,n.revision_id,n."SpaceId" AS space_id,
          CAST(s."Name" AS text) AS space_name,n.title,CAST(n."HeadingId" AS text) AS heading_id,CAST(n."HeadingTitle" AS text) AS heading_title,
          {{SearchSqlText.StripSecrets("substr(n.\"Text\",strpos(n.\"Text\",E'\\n')+1)")}} AS text,
          CAST(row_number() OVER (ORDER BY CASE WHEN n.lexical_score>=1500 THEN n.lexical_score END DESC NULLS LAST,
            CASE WHEN n.focused THEN 0 ELSE 1 END,n.position,n.nth) AS integer) AS rank
        FROM chosen n JOIN "Space" s ON s."Id"=n."SpaceId"
        ORDER BY CASE WHEN n.lexical_score>=1500 THEN n.lexical_score END DESC NULLS LAST,
            CASE WHEN n.focused THEN 0 ELSE 1 END,n.position,n.nth
        LIMIT :take
        """;

    /// <summary>Whether each cited revision is still readable for the actor (available) and whether its page has published another
    /// one since (changed). Columns: page_id, revision_id, available, changed.</summary>
    public static readonly string Status = $$"""
        WITH {{AccessSql.ActorCte}}, ref AS MATERIALIZED (
          SELECT DISTINCT r.page_id,r.revision_id FROM unnest(CAST(:pages AS uuid[]),CAST(:revisions AS uuid[])) AS r(page_id,revision_id)
        ), readable AS MATERIALIZED (
          SELECT r.page_id,r.revision_id,p."SpaceId",p."PublishedRevisionId"
          FROM ref r JOIN "Page" p ON p."Id"=r.page_id CROSS JOIN actor a
          WHERE NOT p."IsTrashed" AND NOT p."IsTemplate" AND p."PublishedRevisionId" IS NOT NULL
            AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e WHERE e."UserId"=:actor AND e."PageId"=p."Id" AND e."Role">=1))
        ), included AS MATERIALIZED (
          SELECT s."Id" FROM (SELECT DISTINCT x."SpaceId" AS "Id" FROM readable x) s WHERE {{AccessSql.SpaceInAi("s.\"Id\"")}}
        )
        SELECT r.page_id,r.revision_id,x.page_id IS NOT NULL AS available,x.page_id IS NOT NULL AND x."PublishedRevisionId"<>r.revision_id AS changed
        FROM ref r LEFT JOIN readable x ON x.page_id=r.page_id AND x.revision_id=r.revision_id
          AND x."SpaceId" IN (SELECT "Id" FROM included)
        """;
}
