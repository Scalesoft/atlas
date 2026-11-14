-- 3.6, no logic in the database (stream S4): the Hub's audit facts (HubChange with its access audit event) and the history of its
-- group bindings are written by the code that changes them (HubHistory), the issuer of a connection is checked there under the
-- connection's row lock, and the identity rules of bindings and identity states are IntegrityRules'. Existing rows stay as they are.
DROP TRIGGER "TR_HubConnectionRevision_Issuer" ON "HubConnectionRevision";
DROP TRIGGER "TR_HubConnectionRevision_Audit" ON "HubConnectionRevision";
DROP TRIGGER "TR_HubGroupBinding_Identity" ON "HubGroupBinding";
DROP TRIGGER "TR_HubGroupBinding_History" ON "HubGroupBinding";
DROP TRIGGER "TR_HubGroupBindingRevision_Audit" ON "HubGroupBindingRevision";
DROP TRIGGER "TR_HubIdentityState_Identity" ON "HubIdentityState";
DROP TRIGGER "TR_HubIdentityState_Audit" ON "HubIdentityState";
DROP FUNCTION atlas_hub_connection_issuer_guard();
DROP FUNCTION atlas_audit_hub_connection();
DROP FUNCTION atlas_hub_binding_identity_guard();
DROP FUNCTION atlas_version_hub_group_binding();
DROP FUNCTION atlas_audit_hub_binding();
DROP FUNCTION atlas_hub_identity_state_guard();
DROP FUNCTION atlas_audit_hub_identity();
