-- 3.6, no logic in the database (stream S4): the history and audit of the security configuration, trusted addresses, protection
-- modes, re-authentication, OIDC settings, vault locations, access requests and secret releases are written by the code that changes
-- them, in the same transaction and with the actor it passes (SecurityHistory, VaultLocationHistory, AccessAuditLog; the version and
-- trusted address rules of the dropped history functions are IntegrityRules'). Existing rows stay as they are.
DROP TRIGGER "TR_SecurityConfiguration_History" ON "SecurityConfiguration";
DROP TRIGGER "TR_SecurityConfigurationRevision_Audit" ON "SecurityConfigurationRevision";
DROP TRIGGER "TR_TrustedAddress_History" ON "TrustedAddress";
DROP TRIGGER "TR_TrustedAddressRevision_Audit" ON "TrustedAddressRevision";
DROP TRIGGER "TR_ProtectionSetting_Audit" ON "ProtectionSetting";
DROP TRIGGER "TR_OidcSettingsRevision_Audit" ON "OidcSettingsRevision";
DROP TRIGGER "TR_VaultLocation_History" ON "VaultLocation";
DROP TRIGGER "TR_VaultAccessRequest_Audit" ON "VaultAccessRequest";
DROP TRIGGER "TR_SecretReleaseEvent_Audit" ON "SecretReleaseEvent";
DROP FUNCTION atlas_version_security_configuration();
DROP FUNCTION atlas_audit_security_configuration_revision();
DROP FUNCTION atlas_version_trusted_address();
DROP FUNCTION atlas_audit_trusted_address_revision();
DROP FUNCTION atlas_audit_protection_setting();
DROP FUNCTION atlas_audit_oidc_settings();
DROP FUNCTION atlas_version_vault_location();
DROP FUNCTION atlas_audit_access_request();
DROP FUNCTION atlas_audit_secret_release();
DROP FUNCTION atlas_record_reauth_audit(uuid);
DROP FUNCTION atlas_record_reauth_window_audit(uuid);
