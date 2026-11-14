-- 3.6-I: how a page is published. Manual (Ručně, as before), Automatic (published when a version closes after inactivity) or Live
-- (readers read the latest saved state live). The space holds the default its pages inherit (NULL on the page); every existing space
-- and page keeps today's behaviour: Manual, inherited. Versioned with the other space settings, as CommentsEnabled is; the defaults
-- stay so that a backup from before this migration restores its rows as Manual.
ALTER TABLE "Space" ADD COLUMN "PublicationMode" varchar(16) NOT NULL DEFAULT 'Manual'
  CONSTRAINT "CK_Space_PublicationMode" CHECK ("PublicationMode" IN ('Manual','Automatic','Live'));
ALTER TABLE "SpaceMetadataRevision" ADD COLUMN "PublicationMode" varchar(16) NOT NULL DEFAULT 'Manual'
  CONSTRAINT "CK_SpaceMetadataRevision_PublicationMode" CHECK ("PublicationMode" IN ('Manual','Automatic','Live'));
ALTER TABLE "Page" ADD COLUMN "PublicationMode" varchar(16) NULL
  CONSTRAINT "CK_Page_PublicationMode" CHECK ("PublicationMode" IN ('Manual','Automatic','Live'));

CREATE OR REPLACE FUNCTION atlas_version_space_metadata() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz;
BEGIN
 IF TG_OP='UPDATE' AND ROW(NEW."Name",NEW."Icon",NEW."Color",NEW."Description",NEW."PinPolicy",NEW."CommentsEnabled",NEW."PublicationMode")
   IS NOT DISTINCT FROM ROW(OLD."Name",OLD."Icon",OLD."Color",OLD."Description",OLD."PinPolicy",OLD."CommentsEnabled",OLD."PublicationMode") THEN RETURN NEW; END IF;
 SELECT "ActiveFrom" INTO previous FROM "SpaceMetadataRevision" WHERE "SpaceId"=NEW."Id" AND "ActiveTo" IS NULL;
 boundary := greatest(clock_timestamp(),previous + interval '1 microsecond');
 UPDATE "SpaceMetadataRevision" SET "ActiveTo"=boundary WHERE "SpaceId"=NEW."Id" AND "ActiveTo" IS NULL;
 INSERT INTO "SpaceMetadataRevision"("Id","SpaceId","Name","Icon","Color","Description","PinPolicy","CommentsEnabled","PublicationMode","ChangedBy","ActiveFrom","ActiveTo")
 VALUES(gen_random_uuid(),NEW."Id",NEW."Name",NEW."Icon",NEW."Color",NEW."Description",NEW."PinPolicy",NEW."CommentsEnabled",NEW."PublicationMode",
   NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
 RETURN NEW;
END $$;

-- Živě: a reader finds a live page by its latest saved state, as its editors do: the drafts search (2026102873) takes a reader of a
-- live page too. Only the access changes.
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
