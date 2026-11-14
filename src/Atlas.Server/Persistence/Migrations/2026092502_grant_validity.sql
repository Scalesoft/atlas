-- Business validity is independent of ActiveFrom/ActiveTo assignment history.
-- Legacy rows retain unbounded validity; no existing historical fact is rewritten.
ALTER TABLE "SpaceGrant" ADD COLUMN "StartAt" timestamptz NULL DEFAULT NULL;
ALTER TABLE "SpaceGrant" ADD COLUMN "ExpiresAt" timestamptz NULL DEFAULT NULL;
ALTER TABLE "SpaceGrant" ADD CONSTRAINT "CK_SpaceGrant_Validity" CHECK (
    ("StartAt" IS NULL OR isfinite("StartAt")) AND
    ("ExpiresAt" IS NULL OR isfinite("ExpiresAt")) AND
    ("StartAt" IS NULL OR "ExpiresAt" IS NULL OR "StartAt" < "ExpiresAt")
);

-- Management includes future and expired current assignments. These views store no copy.
CREATE VIEW "SpaceGrantView" AS
SELECT g."Id", g."SpaceId", g."UserId", g."GroupId", COALESCE(u."DisplayName", ug."Name") AS "Name",
    g."Role", g."ActiveFrom", g."StartAt", g."ExpiresAt"
FROM "SpaceGrant" g
LEFT JOIN "UserAccount" u ON u."Id" = g."UserId"
LEFT JOIN "UserGroup" ug ON ug."Id" = g."GroupId"
WHERE g."ActiveTo" IS NULL;

CREATE VIEW "EffectiveSpaceGrant" AS
SELECT "Id", "SpaceId", "UserId", "GroupId", "Role", "ActiveFrom", "ActiveTo", "StartAt", "ExpiresAt"
FROM "SpaceGrant"
WHERE "ActiveTo" IS NULL
    AND ("StartAt" IS NULL OR "StartAt" <= statement_timestamp())
    AND ("ExpiresAt" IS NULL OR statement_timestamp() < "ExpiresAt");

-- Forward-only: removing business validity would silently make bounded grants permanent.
