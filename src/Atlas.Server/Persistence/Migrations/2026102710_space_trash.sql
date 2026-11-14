-- Space trash: a trashed space keeps its row, grants, vaults,
-- templates, image and pins; its live pages go to the page trash under the same TrashBatchId, so a restore brings both back.
ALTER TABLE "Space" ADD COLUMN "TrashedAt" timestamptz NULL;
ALTER TABLE "Space" ADD COLUMN "TrashedById" uuid NULL REFERENCES "UserAccount"("Id");
ALTER TABLE "Space" ADD COLUMN "TrashBatchId" uuid NULL;
ALTER TABLE "Space" ADD CONSTRAINT "CK_Space_Trash" CHECK (
    ("TrashedAt" IS NULL AND "TrashedById" IS NULL AND "TrashBatchId" IS NULL)
    OR ("TrashedAt" IS NOT NULL AND "TrashedById" IS NOT NULL AND "TrashBatchId" IS NOT NULL));
CREATE INDEX "IX_Space_Trash" ON "Space"("TrashedAt" DESC,"Id") WHERE "TrashedAt" IS NOT NULL;

-- A trashed space is found by nobody, the wiki admin included.
CREATE OR REPLACE FUNCTION atlas_search_spaces(
  p_query text, p_prefix text, p_fts text, p_phrase boolean, p_fuzzy boolean,
  p_actor uuid, p_stamp text, p_space uuid)
RETURNS TABLE(space_id uuid, space_name text, space_icon text, space_color text)
LANGUAGE sql STABLE AS $$
        WITH actor AS MATERIALIZED (
          SELECT ("IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectiveWikiAdmin" h WHERE h."UserId"=p_actor)) AS "IsWikiAdmin" FROM "UserAccount" WHERE "Id"=p_actor AND "IsActive" AND "SecurityStamp"=p_stamp
        ), visible AS MATERIALIZED (
          SELECT s.*,atlas_normalize_search_text(s."Name") AS normalized FROM "Space" s CROSS JOIN actor a
          WHERE s."TrashedAt" IS NULL AND (p_space IS NULL OR s."Id"=p_space) AND (a."IsWikiAdmin" OR EXISTS (
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
