-- 3.6, no logic in the database (stream S4): the append-only and no-delete guards go. The application holds the same rules:
-- IntegrityRules refuses every NHibernate update or delete that breaks one (the messages are the triggers'), and
-- IntegrityRulesConventionTests refuses native SQL that updates or deletes these rows other than by closing an open interval.
-- Existing rows stay as they are.
DROP TRIGGER "TR_AccessAuditEvent_Immutable" ON "AccessAuditEvent";
DROP TRIGGER "TR_ApiActivity_Immutable" ON "ApiActivity";
DROP TRIGGER "TR_BackupDestinationChange_Immutable" ON "BackupDestinationChange";
DROP TRIGGER "TR_HubChange_Immutable" ON "HubChange";
DROP TRIGGER "TR_PageAccessEvent_Immutable" ON "PageAccessEvent";
DROP TRIGGER "TR_RevokedBrowserSession_Immutable" ON "RevokedBrowserSession";
DROP TRIGGER "TR_SecretReleaseEvent_Immutable" ON "SecretReleaseEvent";
DROP TRIGGER "TR_AiProfileRevision_Immutable" ON "AiProfileRevision";
DROP TRIGGER "TR_VaultAccessRequest_Immutable" ON "VaultAccessRequest";
DROP TRIGGER "TR_UserAccountRevision_Immutable" ON "UserAccountRevision";
DROP TRIGGER "TR_UserGroupRevision_Immutable" ON "UserGroupRevision";
DROP TRIGGER "TR_GroupMember_Immutable" ON "GroupMember";
DROP TRIGGER "TR_SpaceGrant_Immutable" ON "SpaceGrant";
DROP TRIGGER "TR_UserApiPermission_Immutable" ON "UserApiPermission";
DROP TRIGGER "TR_ResourceGrant_Immutable" ON "ResourceGrant";
DROP TRIGGER "TR_PageAccessModeRevision_Immutable" ON "PageAccessModeRevision";
DROP TRIGGER "TR_ProtectionSetting_Immutable" ON "ProtectionSetting";
DROP TRIGGER "TR_SecurityConfigurationRevision_Immutable" ON "SecurityConfigurationRevision";
DROP TRIGGER "TR_TrustedAddressRevision_Immutable" ON "TrustedAddressRevision";
DROP TRIGGER "TR_VaultLocationRevision_Immutable" ON "VaultLocationRevision";
DROP TRIGGER "TR_HubConnectionRevision_Immutable" ON "HubConnectionRevision";
DROP TRIGGER "TR_HubGroupBindingRevision_Immutable" ON "HubGroupBindingRevision";
DROP TRIGGER "TR_McpSetting_Immutable" ON "McpSetting";
DROP TRIGGER "TR_GroupAiPolicy_Immutable" ON "GroupAiPolicy";
DROP TRIGGER "TR_UserAiPolicy_Immutable" ON "UserAiPolicy";
DROP TRIGGER "TR_SecurityConfiguration_NoDelete" ON "SecurityConfiguration";
DROP TRIGGER "TR_TrustedAddress_NoDelete" ON "TrustedAddress";
DROP FUNCTION atlas_access_audit_immutable();
DROP FUNCTION atlas_access_audit_history_guard();
DROP FUNCTION atlas_ai_revision_immutable();
DROP FUNCTION atlas_access_request_guard();
DROP FUNCTION atlas_security_configuration_guard();
DROP FUNCTION atlas_trusted_address_guard();
