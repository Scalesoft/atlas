-- Live derived authority only; persisted local and OIDC sources stay on UserAccount.
CREATE VIEW "EffectiveWikiAdmin" AS
SELECT m."UserId", max(m."VerifiedUntil") AS "HubVerifiedUntil"
FROM "EffectiveGroupMember" m
JOIN "HubGroupBinding" b ON b."GroupId"=m."GroupId" AND b."GrantsWikiAdmin"
GROUP BY m."UserId";

CREATE OR REPLACE VIEW "EffectivePageAccess" AS
SELECT access."UserId", access."PageId", access."SpaceId", max(access."Role") AS "Role"
FROM (
    SELECT u."Id" AS "UserId", p."Id" AS "PageId", p."SpaceId", 3 AS "Role"
    FROM "UserAccount" u CROSS JOIN "Page" p
    WHERE u."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=u."Id")
    UNION ALL
    SELECT g."UserId", p."Id", p."SpaceId", CASE WHEN p."IsTemplate" AND g."Role"<3 THEN 1 ELSE g."Role" END
    FROM "EffectiveSpaceGrant" g
    JOIN "Page" p ON p."SpaceId"=g."SpaceId" AND (NOT p."OwnAccess" OR g."Role"=3)
    WHERE g."UserId" IS NOT NULL
    UNION ALL
    SELECT m."UserId", p."Id", p."SpaceId", CASE WHEN p."IsTemplate" AND g."Role"<3 THEN 1 ELSE g."Role" END
    FROM "EffectiveGroupMember" m
    JOIN "EffectiveSpaceGrant" g ON g."GroupId"=m."GroupId"
    JOIN "Page" p ON p."SpaceId"=g."SpaceId" AND (NOT p."OwnAccess" OR g."Role"=3)
    UNION ALL
    SELECT g."UserId", p."Id", p."SpaceId", CASE WHEN p."IsTemplate" THEN 1 ELSE g."Role" END
    FROM "EffectiveResourceGrant" g JOIN "Page" p ON p."Id"=g."PageId"
    WHERE g."UserId" IS NOT NULL
    UNION ALL
    SELECT m."UserId", p."Id", p."SpaceId", CASE WHEN p."IsTemplate" THEN 1 ELSE g."Role" END
    FROM "EffectiveGroupMember" m JOIN "EffectiveResourceGrant" g ON g."GroupId"=m."GroupId"
    JOIN "Page" p ON p."Id"=g."PageId"
) access
JOIN "UserAccount" active ON active."Id"=access."UserId" AND active."IsActive"
GROUP BY access."UserId", access."PageId", access."SpaceId";

-- Forward-only: authority changes must be rolled back through explicit source migration.

-- WP3 S3: search decides on every page through the EffectivePageAccess view (D5) instead of the space role. Each search
-- function reads the actor's accessible pages once (a UserId-filtered, materialized CTE; the view pushes that filter into
-- every grant branch) and filters by it before ranking, the window and snippets, so a hidden page leaves no title, snippet
-- or count. The filter stays an OR'ed IN, which PostgreSQL runs as a hashed subplan: a join lets a misestimated candidate
-- set pick a nested loop over the CTE (measured 400 ms instead of 17 ms). The view gives a WikiAdmin every page; the
-- IsWikiAdmin short cut only skips building that set.
-- Published scopes need a page role >= 1, the working scope >= 2 plus the unchanged owner/WikiAdmin draft rule; the
-- space-suggestion scope also shows a space in which the account can list a page (D8: name, icon and colour only).
-- Ranking, thresholds and result shapes are unchanged; atlas_search_all composes the redefined functions as before.
-- New migration only; historical function definitions remain immutable.

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
            AND (to_tsvector('simple',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts)
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE to_tsvector('simple',atlas_normalize_search_text(alias)) @@ to_tsquery('simple',p_fts)
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
            AND (to_tsvector('simple',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts)
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        LEFT JOIN LATERAL (
          SELECT alias FROM jsonb_array_elements_text(d."Aliases") AS alias
          WHERE to_tsvector('simple',atlas_normalize_search_text(alias)) @@ to_tsquery('simple',p_fts)
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
            AND (to_tsvector('simple',heading->>'normalizedTitle') @@ to_tsquery('simple',p_fts)
              OR (p_fuzzy AND p_query <% (heading->>'normalizedTitle')))
          ORDER BY CASE WHEN heading->>'normalizedTitle'=p_query THEN 2 ELSE 1 END DESC,
            word_similarity(p_query,heading->>'normalizedTitle') DESC LIMIT 1
        ) h ON true
        ORDER BY CASE WHEN p_latest THEN r."ChangedAt" END DESC NULLS LAST,r.score DESC,r."Title",r."PageId"
$$;

CREATE OR REPLACE FUNCTION atlas_search_spaces(
  p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean,
  p_actor uuid, p_stamp text, p_space uuid)
RETURNS TABLE(space_id uuid, space_name text, space_icon text, space_color text)
LANGUAGE sql STABLE AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), visible AS MATERIALIZED (
          SELECT s.*,atlas_normalize_search_text(s."Name") AS normalized FROM "Space" s CROSS JOIN actor a
          WHERE (p_space IS NULL OR s."Id"=p_space) AND (a."IsWikiAdmin" OR EXISTS (
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

-- Capabilities may never outlive the issuer's currently effective Admin authority.
-- VOLATILE observes the membership/grant mutation that fired the trigger.
CREATE OR REPLACE FUNCTION atlas_share_creator_can_share_until(actor uuid, space uuid, required_expiry timestamptz)
RETURNS boolean LANGUAGE sql VOLATILE AS $$
 SELECT EXISTS (SELECT 1 FROM "UserAccount" u WHERE u."Id"=actor AND u."IsActive" AND
   (u."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=actor AND required_expiry <= h."HubVerifiedUntil") OR EXISTS (SELECT 1 FROM "EffectiveSpaceGrant" g WHERE g."SpaceId"=space AND g."Role"=3
     AND (g."ExpiresAt" IS NULL OR required_expiry <= g."ExpiresAt")
     AND (g."UserId"=actor OR EXISTS (SELECT 1 FROM "EffectiveGroupMember" m
       WHERE m."UserId"=actor AND m."GroupId"=g."GroupId" AND m."ActiveTo" IS NULL AND (m."VerifiedUntil" IS NULL OR required_expiry <= m."VerifiedUntil"))))))
$$;


CREATE OR REPLACE FUNCTION atlas_share_creator_can_share_page_until(actor uuid, space uuid, page uuid, required_expiry timestamptz)
RETURNS boolean LANGUAGE sql VOLATILE AS $$
 SELECT atlas_share_creator_can_share_until(actor, space, required_expiry)
   OR EXISTS (SELECT 1 FROM "UserAccount" u
     JOIN "EffectiveResourceGrant" g ON g."PageId"=page AND g."Role"=3
     WHERE u."Id"=actor AND u."IsActive"
       AND (g."ExpiresAt" IS NULL OR required_expiry <= g."ExpiresAt")
       AND (g."UserId"=actor OR EXISTS (SELECT 1 FROM "EffectiveGroupMember" m
         WHERE m."UserId"=actor AND m."GroupId"=g."GroupId" AND m."ActiveTo" IS NULL AND (m."VerifiedUntil" IS NULL OR required_expiry <= m."VerifiedUntil"))))
$$;


