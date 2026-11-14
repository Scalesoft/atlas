-- Atlas 4.0: migration 2026110684 mapped the former draft, publish and direct token scopes and account levels to write without a
-- word (the mapping is the owner's to decide). As 2026100600 did, the security overview asks a WikiAdmin to review them (ApiLevelsReview,
-- „Zkontrolujte úrovně osobního API po upgradu“) when such tokens are still valid and no review is pending. Every write scope or level
-- older than 2026110684 is a former one (write did not exist before it); those created since are left out, so an installation that
-- applied 2026110684 earlier counts only what it widened.
INSERT INTO "ApiLevelsReview"("Id","Accounts","Tokens")
SELECT gen_random_uuid(), counts.accounts, counts.tokens
FROM (SELECT
        (SELECT count(*) FROM "UserApiPermission" p
         WHERE p."ActiveTo" IS NULL AND p."Level" = 'write' AND p."ActiveFrom" < v."AppliedAt")::integer AS accounts,
        (SELECT count(*) FROM "PersonalApiToken" t
         WHERE t."ActiveTo" IS NULL AND t."ExpiresAt" > now() AND t."ActiveFrom" < v."AppliedAt"
           AND EXISTS (SELECT 1 FROM "PersonalApiTokenScope" s WHERE s."TokenId" = t."Id" AND s."Scope" = 'write'))::integer AS tokens
      FROM "SchemaVersion" v WHERE v."Version" = 2026110684) counts
WHERE counts.tokens > 0 AND NOT EXISTS (SELECT 1 FROM "ApiLevelsReview" WHERE "ConfirmedAt" IS NULL);
