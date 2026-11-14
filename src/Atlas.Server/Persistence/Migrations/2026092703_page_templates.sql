ALTER TABLE "Page" ADD COLUMN "IsTemplate" boolean NOT NULL DEFAULT false;

-- Mirror ResourceAccessPolicy: a template keeps normal read rights, while only a wiki administrator or
-- space default administrator may edit/manage/share it. Direct page grants never grant template management.
CREATE OR REPLACE VIEW "EffectivePageAccess" AS
SELECT access."UserId", access."PageId", access."SpaceId", max(access."Role") AS "Role"
FROM (
    SELECT u."Id" AS "UserId", p."Id" AS "PageId", p."SpaceId", 3 AS "Role"
    FROM "UserAccount" u CROSS JOIN "Page" p
    WHERE u."IsWikiAdmin"
    UNION ALL
    SELECT g."UserId", p."Id", p."SpaceId",
        CASE WHEN p."IsTemplate" AND g."Role" < 3 THEN 1 ELSE g."Role" END
    FROM "EffectiveSpaceGrant" g
    JOIN "Page" p ON p."SpaceId" = g."SpaceId" AND (NOT p."OwnAccess" OR g."Role" = 3)
    WHERE g."UserId" IS NOT NULL
    UNION ALL
    SELECT m."UserId", p."Id", p."SpaceId",
        CASE WHEN p."IsTemplate" AND g."Role" < 3 THEN 1 ELSE g."Role" END
    FROM "GroupMember" m
    JOIN "EffectiveSpaceGrant" g ON g."GroupId" = m."GroupId"
    JOIN "Page" p ON p."SpaceId" = g."SpaceId" AND (NOT p."OwnAccess" OR g."Role" = 3)
    WHERE m."ActiveTo" IS NULL
    UNION ALL
    SELECT g."UserId", p."Id", p."SpaceId",
        CASE WHEN p."IsTemplate" THEN 1 ELSE g."Role" END
    FROM "EffectiveResourceGrant" g
    JOIN "Page" p ON p."Id" = g."PageId"
    WHERE g."UserId" IS NOT NULL
    UNION ALL
    SELECT m."UserId", p."Id", p."SpaceId",
        CASE WHEN p."IsTemplate" THEN 1 ELSE g."Role" END
    FROM "GroupMember" m
    JOIN "EffectiveResourceGrant" g ON g."GroupId" = m."GroupId"
    JOIN "Page" p ON p."Id" = g."PageId"
    WHERE m."ActiveTo" IS NULL
) access
JOIN "UserAccount" active ON active."Id" = access."UserId" AND active."IsActive"
GROUP BY access."UserId", access."PageId", access."SpaceId";
