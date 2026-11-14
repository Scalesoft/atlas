-- 3.6, no logic in the database (stream S4): the access audit of API, MCP and assistant writes, the MCP switch, the AI access
-- policies, permanent deletes and backup destinations is written by the code that records them, with their actor
-- (AccessAuditLog, RecordApiActivityHandler); the private note of an AI feedback goes with its message in
-- ConversationStore.DeleteAsync, the one path that deletes messages. Existing rows stay as they are.
DROP TRIGGER "TR_ApiActivity_Audit" ON "ApiActivity";
DROP TRIGGER "TR_McpSetting_Audit" ON "McpSetting";
DROP TRIGGER "TR_GroupAiPolicy_Audit" ON "GroupAiPolicy";
DROP TRIGGER "TR_UserAiPolicy_Audit" ON "UserAiPolicy";
DROP TRIGGER "TR_ObjectPurgeEvent_Audit" ON "ObjectPurgeEvent";
DROP TRIGGER "TR_BackupDestinationChange_Audit" ON "BackupDestinationChange";
DROP TRIGGER "TR_AiMessage_ScrubFeedbackNote" ON "AiMessage";
DROP FUNCTION atlas_audit_api_activity();
DROP FUNCTION atlas_audit_mcp_setting();
DROP FUNCTION atlas_audit_group_ai_policy();
DROP FUNCTION atlas_audit_user_ai_policy();
DROP FUNCTION atlas_audit_object_purge();
DROP FUNCTION atlas_audit_backup_destination();
DROP FUNCTION atlas_ai_feedback_scrub_note();
