-- 3.6, no logic in the database (stream S4): the history, audit and share-link closing of accounts, groups, memberships, space and
-- page grants, API permissions, page access modes and share links are written by the code that changes them, in the same
-- transaction and with the actor it passes (AccountHistory, GroupHistory, PageAccessModeHistory, AccessAuditLog, ShareRevocation;
-- AccessAuditCoverageTests keeps every writer calling them, the pins in tests/AccessAudit keep the rows as the triggers wrote them).
-- Existing rows stay as they are: nothing here reads or rewrites history, audit or links.
DROP TRIGGER "TR_UserAccount_History" ON "UserAccount";
DROP TRIGGER "TR_UserAccount_RevokeShares" ON "UserAccount";
DROP TRIGGER "TR_UserAccountRevision_Audit" ON "UserAccountRevision";
DROP TRIGGER "TR_UserGroup_History" ON "UserGroup";
DROP TRIGGER "TR_UserGroupRevision_Audit" ON "UserGroupRevision";
DROP TRIGGER "TR_GroupMember_Audit" ON "GroupMember";
DROP TRIGGER "TR_GroupMember_RevokeShares" ON "GroupMember";
DROP TRIGGER "TR_SpaceGrant_Audit" ON "SpaceGrant";
DROP TRIGGER "TR_SpaceGrant_RevokeShares" ON "SpaceGrant";
DROP TRIGGER "TR_UserApiPermission_Audit" ON "UserApiPermission";
DROP TRIGGER "TR_ResourceGrant_Audit" ON "ResourceGrant";
DROP TRIGGER "TR_ResourceGrant_RevokeShares" ON "ResourceGrant";
DROP TRIGGER "TR_Page_AccessModeHistory" ON "Page";
DROP TRIGGER "TR_Page_RevokeShares" ON "Page";
DROP TRIGGER "TR_PageShareLink_Audit" ON "PageShareLink";
DROP FUNCTION atlas_version_user_account();
DROP FUNCTION atlas_revoke_account_shares();
DROP FUNCTION atlas_audit_account_revision();
DROP FUNCTION atlas_version_user_group();
DROP FUNCTION atlas_audit_group_revision();
DROP FUNCTION atlas_audit_group_member();
DROP FUNCTION atlas_revoke_membership_shares();
DROP FUNCTION atlas_audit_space_grant();
DROP FUNCTION atlas_revoke_grant_shares();
DROP FUNCTION atlas_audit_api_permission();
DROP FUNCTION atlas_audit_resource_grant();
DROP FUNCTION atlas_revoke_resource_grant_shares();
DROP FUNCTION atlas_version_page_access_mode();
DROP FUNCTION atlas_revoke_page_shares();
DROP FUNCTION atlas_audit_page_share();
DROP FUNCTION atlas_record_credential_audit(uuid, text);
DROP FUNCTION atlas_share_creator_is_admin(uuid, uuid);
DROP FUNCTION atlas_share_creator_can_share_page_until(uuid, uuid, uuid, timestamptz);
DROP FUNCTION atlas_share_creator_can_share_until(uuid, uuid, timestamptz);
