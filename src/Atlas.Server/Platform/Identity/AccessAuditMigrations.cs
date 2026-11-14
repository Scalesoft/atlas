using FluentMigrator;

namespace Atlas.Server.Platform.Identity;

// 3.6, no logic in the database (stream S4): the security and audit triggers go, the code writes what they wrote.

[Migration(2026110941, "Accounts, groups, grants, access modes and share links: history, audit and share closing in code")]
public sealed class RightsAuditInCodeMigration() : EmbeddedSchemaMigration("2026110941_rights_audit_in_code.sql");

[Migration(2026110942, "Security configuration, trusted addresses, protection, re-authentication, OIDC and vaults: history and audit in code")]
public sealed class SecurityAuditInCodeMigration() : EmbeddedSchemaMigration("2026110942_security_audit_in_code.sql");

[Migration(2026110943, "Hub: audit facts, binding history and identity guards in code")]
public sealed class HubAuditInCodeMigration() : EmbeddedSchemaMigration("2026110943_hub_audit_in_code.sql");

[Migration(2026110944, "API, MCP, assistant, AI policy, purge and backup audit in code; the feedback note goes with its message in code")]
public sealed class ToolAuditInCodeMigration() : EmbeddedSchemaMigration("2026110944_tool_audit_in_code.sql");

[Migration(2026110945, "The append-only and no-delete guards of audit and access history move into the application (IntegrityRules)")]
public sealed class IntegrityGuardsInCodeMigration() : EmbeddedSchemaMigration("2026110945_integrity_guards_in_code.sql");

[Migration(2026110946, "Access predicates (space AI inclusion, object grant role, page creation) in query text; their functions go")]
public sealed class AccessFunctionsInCodeMigration() : EmbeddedSchemaMigration("2026110946_access_functions_in_code.sql");
