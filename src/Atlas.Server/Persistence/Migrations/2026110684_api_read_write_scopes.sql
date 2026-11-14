-- 3.6 (owner's decision): personal API tokens have only two scopes, read and write; 3.6 has no drafts, a write is a live edit like a
-- person's and the page's publication setting (Ručně/Automaticky/Živě) decides what readers see. Mapping, for tokens and for the
-- account ceilings (UserApiPermission.Level) alike:
--   read → read
--   draft, publish, direct → write (a token that could only write drafts can now also publish, move and trash pages, always within
--                            its owner's rights and never in a space excluded from AI)
--   none → none
-- Every row is rewritten, the closed history of the ceiling included, so the stored values stay just these three; the audit of an
-- old change between two former write levels then reads write → write.

-- Ceilings. The history guard lets an UPDATE change ActiveTo only, so the backfill runs with it disabled; the migration transaction
-- re-enables it before anything else can write.
ALTER TABLE "UserApiPermission" DROP CONSTRAINT "CK_UserApiPermission_Level";
ALTER TABLE "UserApiPermission" DISABLE TRIGGER "TR_UserApiPermission_Immutable";
UPDATE "UserApiPermission" SET "Level" = 'write' WHERE "Level" IN ('draft','publish','direct');
ALTER TABLE "UserApiPermission" ENABLE TRIGGER "TR_UserApiPermission_Immutable";
ALTER TABLE "UserApiPermission" ADD CONSTRAINT "CK_UserApiPermission_Level" CHECK ("Level" IN ('none','read','write'));

-- Token scopes: one write row for every token that had any former write scope, then the former rows go.
ALTER TABLE "PersonalApiTokenScope" DROP CONSTRAINT "CK_PersonalApiTokenScope_Scope";
INSERT INTO "PersonalApiTokenScope"("TokenId","Scope")
SELECT DISTINCT "TokenId",'write' FROM "PersonalApiTokenScope" WHERE "Scope" IN ('draft','publish','direct')
ON CONFLICT DO NOTHING;
DELETE FROM "PersonalApiTokenScope" WHERE "Scope" IN ('draft','publish','direct');
ALTER TABLE "PersonalApiTokenScope" ADD CONSTRAINT "CK_PersonalApiTokenScope_Scope" CHECK ("Scope" IN ('read','write'));
