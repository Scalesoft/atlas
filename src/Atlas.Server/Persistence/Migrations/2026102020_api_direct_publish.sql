-- Direct publication over MCP (owner's decision 2026-10-20): the level and the token scope "direct" above publish let atlas_publish
-- publish without the owner's confirmation in the web. Nothing existing changes: no account has the level and no token the scope
-- until a WikiAdmin grants it and the owner issues a token with it.
ALTER TABLE "UserApiPermission" DROP CONSTRAINT "CK_UserApiPermission_Level",
    ADD CONSTRAINT "CK_UserApiPermission_Level" CHECK ("Level" IN ('none','read','draft','publish','direct'));
ALTER TABLE "PersonalApiTokenScope" DROP CONSTRAINT "CK_PersonalApiTokenScope_Scope",
    ADD CONSTRAINT "CK_PersonalApiTokenScope_Scope" CHECK ("Scope" IN ('read','draft','publish','direct'));
