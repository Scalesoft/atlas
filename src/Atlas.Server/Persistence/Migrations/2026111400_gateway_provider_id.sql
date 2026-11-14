-- Since 5.0 the company OpenAI-compatible provider of 2026100840 is stored as 'gateway'. 'strata' is its legacy id of revisions stored
-- before 5.0: the rows convert here (an installation that ran 2026100840 before 5.0 has the constraint "CK_AiProfileRevision_Strata",
-- a new one "CK_AiProfileRevision_Gateway"). The checks keep accepting the legacy id so that a backup of an older installation still
-- restores; its restore runs the same UPDATE again (InstallationModules.DataMigrations), and the code knows only 'gateway'.
ALTER TABLE "AiProfileRevision" DROP CONSTRAINT IF EXISTS "CK_AiProfileRevision_Strata";
ALTER TABLE "AiProfileRevision" DROP CONSTRAINT IF EXISTS "CK_AiProfileRevision_Gateway";
ALTER TABLE "AiProfileRevision" DROP CONSTRAINT "CK_AiProfileRevision_Provider";
UPDATE "AiProfileRevision" SET "Provider" = 'gateway' WHERE lower("Provider") = 'strata';
ALTER TABLE "AiProfileRevision" ADD CONSTRAINT "CK_AiProfileRevision_Provider"
    CHECK ("Provider" IN ('openai-compatible', 'anthropic', 'gateway', 'strata'));
ALTER TABLE "AiProfileRevision" ADD CONSTRAINT "CK_AiProfileRevision_Gateway"
    CHECK ("Provider" NOT IN ('gateway', 'strata') OR ("Location" = 'company' AND "Kind" IN ('chat', 'quick')));
