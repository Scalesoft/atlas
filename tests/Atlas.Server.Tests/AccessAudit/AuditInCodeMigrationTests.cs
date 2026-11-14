using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Events.OutboxTests;

namespace Atlas.Server.Tests.AccessAudit;

/// <summary>
/// The migrations of 3.6 that drop the security and audit triggers (stream S4, no logic in the database) over existing rows: every
/// history, audit and share row stays exactly as it was, the triggers and functions are gone, and bare SQL no longer writes what the
/// code writes now (the pins and IntegrityRulesTests prove the code).
/// </summary>
public sealed class AuditInCodeMigrationTests
{
    private const string Admin = "00000000-0000-0000-0000-000000000001", Member = "00000000-0000-0000-0000-000000000002";
    private const string Group = "00000000-0000-0000-0000-0000000000b1", Space = "00000000-0000-0000-0000-0000000000c1";
    private const string Page = "00000000-0000-0000-0000-0000000000d1";

    /// <summary>Migration 2026110941: accounts, groups, memberships, grants, page access modes and share links.</summary>
    [PostgreSqlFact]
    public async Task Migration_2026110941_keeps_the_history_and_audit_of_rights_and_leaves_their_writing_to_the_code()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_rights_in_code_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110941);
        await ExecuteAsync(schema.ConnectionString, $"""
            SELECT set_config('scaleatlas.actor_id','{Admin}',false);
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Admin}','admin','Admin',true,true,'a'),('{Member}','member','Member',false,true,'m');
            UPDATE "UserAccount" SET "DisplayName"='Člen' WHERE "Id"='{Member}';
            INSERT INTO "UserGroup"("Id","Name") VALUES('{Group}','Tým');
            INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom") VALUES(gen_random_uuid(),'{Group}','{Member}',now());
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','Prostor','book-open','blue');
            INSERT INTO "SpaceGrant"("Id","SpaceId","GroupId","Role","ActiveFrom") VALUES(gen_random_uuid(),'{Space}','{Group}',3,now());
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','{Space}','{Admin}',1);
            UPDATE "Page" SET "OwnAccess"=true WHERE "Id"='{Page}';
            INSERT INTO "ResourceGrant"("Id","PageId","UserId","Role") VALUES(gen_random_uuid(),'{Page}','{Member}',3);
            """);
        var before = await SnapshotAsync(schema.ConnectionString);
        Assert.Contains("membership.added", before);

        await ApplyBeforeAsync(schema.ConnectionString, 2026110942, from: 2026110941);

        Assert.Equal(before, await SnapshotAsync(schema.ConnectionString));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM pg_trigger WHERE tgname IN ('TR_UserAccount_History','TR_UserAccount_RevokeShares','TR_UserAccountRevision_Audit',
                'TR_UserGroup_History','TR_UserGroupRevision_Audit','TR_GroupMember_Audit','TR_GroupMember_RevokeShares','TR_SpaceGrant_Audit',
                'TR_SpaceGrant_RevokeShares','TR_UserApiPermission_Audit','TR_ResourceGrant_Audit','TR_ResourceGrant_RevokeShares',
                'TR_Page_AccessModeHistory','TR_Page_RevokeShares','TR_PageShareLink_Audit')
            """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM pg_proc WHERE proname IN ('atlas_version_user_account','atlas_revoke_account_shares','atlas_audit_account_revision',
                'atlas_version_user_group','atlas_audit_group_revision','atlas_audit_group_member','atlas_revoke_membership_shares',
                'atlas_audit_space_grant','atlas_revoke_grant_shares','atlas_audit_api_permission','atlas_audit_resource_grant',
                'atlas_revoke_resource_grant_shares','atlas_version_page_access_mode','atlas_revoke_page_shares','atlas_audit_page_share',
                'atlas_record_credential_audit','atlas_share_creator_is_admin','atlas_share_creator_can_share_page_until',
                'atlas_share_creator_can_share_until')
            """));
        // Bare SQL writes no history and no audit any more: the code that changes a right writes them.
        var history = await SnapshotAsync(schema.ConnectionString, history: true);
        await ExecuteAsync(schema.ConnectionString, $"""
            UPDATE "UserAccount" SET "DisplayName"='Jiné jméno' WHERE "Id"='{Member}';
            UPDATE "GroupMember" SET "ActiveTo"=clock_timestamp() WHERE "UserId"='{Member}';
            UPDATE "Page" SET "OwnAccess"=false WHERE "Id"='{Page}';
            """);
        Assert.Equal(history, await SnapshotAsync(schema.ConnectionString, history: true));
    }

    /// <summary>Migration 2026110942: the security configuration, trusted addresses, protection modes, OIDC settings and vault locations.</summary>
    [PostgreSqlFact]
    public async Task Migration_2026110942_keeps_the_security_history_and_audit_and_leaves_their_writing_to_the_code()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_security_in_code_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110942);
        const string Address = "00000000-0000-0000-0000-0000000000e1", Location = "00000000-0000-0000-0000-0000000000f1";
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Admin}','admin','Admin',true,true,'a');
            SELECT set_config('scaleatlas.actor_id','{Admin}',false);
            UPDATE "SecurityConfiguration" SET "Version"=2, "ProxyHopLimit"=2 WHERE "Id"=1;
            INSERT INTO "TrustedAddress"("Id","Purpose","Name","Kind","Range","IsActive") VALUES('{Address}','network','Kancelář','lan','192.168.10.0/24',true);
            INSERT INTO "VaultLocation"("Id","Name","Kind","Root") VALUES('{Location}','Místní','local','/srv/vaults');
            UPDATE "VaultLocation" SET "Name"='Místní trezory' WHERE "Id"='{Location}';
            """);
        string[] tables = ["SecurityConfigurationRevision", "TrustedAddressRevision", "VaultLocationRevision", "ProtectionSetting", "AccessAuditEvent"];
        var before = await SnapshotAsync(schema.ConnectionString, tables);
        Assert.Contains("trusted_network.created", before);

        await ApplyBeforeAsync(schema.ConnectionString, 2026110943, from: 2026110942);

        Assert.Equal(before, await SnapshotAsync(schema.ConnectionString, tables));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM pg_trigger WHERE tgname IN ('TR_SecurityConfiguration_History','TR_SecurityConfigurationRevision_Audit',
                'TR_TrustedAddress_History','TR_TrustedAddressRevision_Audit','TR_ProtectionSetting_Audit','TR_OidcSettingsRevision_Audit',
                'TR_VaultLocation_History','TR_VaultAccessRequest_Audit','TR_SecretReleaseEvent_Audit')
            """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM pg_proc WHERE proname IN ('atlas_version_security_configuration','atlas_audit_security_configuration_revision',
                'atlas_version_trusted_address','atlas_audit_trusted_address_revision','atlas_audit_protection_setting','atlas_audit_oidc_settings',
                'atlas_version_vault_location','atlas_audit_access_request','atlas_audit_secret_release','atlas_record_reauth_audit',
                'atlas_record_reauth_window_audit')
            """));
        // Bare SQL writes no history and no audit any more.
        await ExecuteAsync(schema.ConnectionString, $"""
            UPDATE "SecurityConfiguration" SET "Version"=3 WHERE "Id"=1;
            UPDATE "TrustedAddress" SET "Name"='Pobočka' WHERE "Id"='{Address}';
            UPDATE "VaultLocation" SET "Name"='Jinde' WHERE "Id"='{Location}';
            """);
        Assert.Equal(before, await SnapshotAsync(schema.ConnectionString, tables));
    }

    /// <summary>Migration 2026110943: the Hub's connection, bindings and observed identities.</summary>
    [PostgreSqlFact]
    public async Task Migration_2026110943_keeps_the_hub_audit_and_binding_history_and_leaves_their_writing_to_the_code()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_hub_in_code_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110943);
        const string Connection = "00000000-0000-0000-0000-0000000000a9", Identity = "00000000-0000-0000-0000-0000000000a8";
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Admin}','admin','Admin',true,true,'a'),('{Member}','member','Member',false,true,'m');
            INSERT INTO "UserGroup"("Id","Name") VALUES('{Group}','Hub');
            SELECT set_config('scaleatlas.actor_id','{Admin}',false);
            INSERT INTO "HubConnection"("Id") VALUES('{Connection}');
            INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
              VALUES(gen_random_uuid(),'{Connection}','https://hub.example.test','https://hub.example.test','p',2,'{Admin}',now()-interval '1 hour');
            INSERT INTO "HubGroupBinding"("GroupId","ConnectionId","ExternalGroupId") VALUES('{Group}','{Connection}','opaque-group');
            UPDATE "HubGroupBinding" SET "GrantsWikiAdmin"=true WHERE "GroupId"='{Group}';
            INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom") VALUES('{Identity}','{Member}','https://hub.example.test','opaque-user',now()-interval '1 hour');
            INSERT INTO "HubIdentityState"("ExternalIdentityId","UserId","ConnectionId","ExternalUserId","SnapshotStartedAt","VerifiedUntil","LastSuccessAt","LastAttemptAt","UpstreamAccountState","Generation")
              VALUES('{Identity}','{Member}','{Connection}','opaque-user',now(),now()+interval '5 minutes',now(),now(),1,1);
            """);
        string[] tables = ["HubChange", "HubGroupBindingRevision", "AccessAuditEvent"];
        var before = await SnapshotAsync(schema.ConnectionString, tables);
        Assert.Contains("identity.observed", before);

        await ApplyBeforeAsync(schema.ConnectionString, 2026110944, from: 2026110943);

        Assert.Equal(before, await SnapshotAsync(schema.ConnectionString, tables));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM pg_trigger WHERE tgname IN ('TR_HubConnectionRevision_Issuer','TR_HubConnectionRevision_Audit','TR_HubGroupBinding_Identity',
                'TR_HubGroupBinding_History','TR_HubGroupBindingRevision_Audit','TR_HubIdentityState_Identity','TR_HubIdentityState_Audit')
            """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM pg_proc WHERE proname IN ('atlas_hub_connection_issuer_guard','atlas_audit_hub_connection','atlas_hub_binding_identity_guard',
                'atlas_version_hub_group_binding','atlas_audit_hub_binding','atlas_hub_identity_state_guard','atlas_audit_hub_identity')
            """));
        // Bare SQL writes no history and no audit any more.
        await ExecuteAsync(schema.ConnectionString, $"""
            UPDATE "HubGroupBinding" SET "Enabled"=false WHERE "GroupId"='{Group}';
            UPDATE "HubIdentityState" SET "UpstreamAccountState"=2 WHERE "ExternalIdentityId"='{Identity}';
            """);
        Assert.Equal(before, await SnapshotAsync(schema.ConnectionString, tables));
    }

    /// <summary>Migration 2026110944: API activity, the MCP switch and backup destinations (purges, AI policies and the feedback note
    /// alike; their writers are pinned in ToolAuditPinTests, PermanentDeleteTests and AiFeedbackTests).</summary>
    [PostgreSqlFact]
    public async Task Migration_2026110944_keeps_the_tool_audit_and_leaves_its_writing_to_the_code()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_tool_in_code_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110944);
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Admin}','admin','Admin',true,true,'a');
            INSERT INTO "ApiActivity"("ActorId","Channel","Operation","Outcome") VALUES('{Admin}','assistant','atlas_publish','ok');
            INSERT INTO "McpSetting"("Id","Enabled","ChangedById","ActiveFrom") VALUES(gen_random_uuid(),true,'{Admin}',now());
            INSERT INTO "BackupDestinationChange"("ActorId","Kind","Destination") VALUES('{Admin}','git.remote','git@example.test:a.git');
            """);
        string[] tables = ["ApiActivity", "McpSetting", "BackupDestinationChange", "AccessAuditEvent"];
        var before = await SnapshotAsync(schema.ConnectionString, tables);
        Assert.Contains("assistant.publish", before);

        await ApplyBeforeAsync(schema.ConnectionString, 2026110945, from: 2026110944);

        Assert.Equal(before, await SnapshotAsync(schema.ConnectionString, tables));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM pg_trigger WHERE tgname IN ('TR_ApiActivity_Audit','TR_McpSetting_Audit','TR_GroupAiPolicy_Audit','TR_UserAiPolicy_Audit',
                'TR_ObjectPurgeEvent_Audit','TR_BackupDestinationChange_Audit','TR_AiMessage_ScrubFeedbackNote')
            """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM pg_proc WHERE proname IN ('atlas_audit_api_activity','atlas_audit_mcp_setting','atlas_audit_group_ai_policy',
                'atlas_audit_user_ai_policy','atlas_audit_object_purge','atlas_audit_backup_destination','atlas_ai_feedback_scrub_note')
            """));
        // Bare SQL writes no audit any more.
        var audit = await SnapshotAsync(schema.ConnectionString, ["AccessAuditEvent"]);
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "BackupDestinationChange"("ActorId","Kind","Destination") VALUES('{Admin}','backup.key','key-1');
            INSERT INTO "ApiActivity"("ActorId","Channel","Operation","Outcome") VALUES('{Admin}','assistant','atlas_publish','ok');
            """);
        Assert.Equal(audit, await SnapshotAsync(schema.ConnectionString, ["AccessAuditEvent"]));
    }

    /// <summary>Migration 2026110945: the guards go and leave every row as it was; from then on the application holds the rules
    /// (IntegrityRulesTests, IntegrityRulesConventionTests), and no trigger or function of stream S4 is left in the schema.</summary>
    [PostgreSqlFact]
    public async Task Migration_2026110945_drops_the_guards_and_keeps_every_row()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_guards_in_code_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110945);
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Admin}','admin','Admin',true,true,'a');
            INSERT INTO "UserAccountRevision"("Id","UserId","Username","DisplayName","IsActive","IsWikiAdmin","ActiveFrom") VALUES(gen_random_uuid(),'{Admin}','admin','Admin',true,true,now());
            INSERT INTO "AccessAuditEvent"("ActorId","Action","AccountRevisionId") SELECT '{Admin}','account.created',"Id" FROM "UserAccountRevision";
            """);
        string[] tables = ["UserAccountRevision", "AccessAuditEvent", "SecurityConfiguration", "SecurityConfigurationRevision", "ProtectionSetting"];
        var before = await SnapshotAsync(schema.ConnectionString, tables);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(schema.ConnectionString, """DELETE FROM "AccessAuditEvent" """));

        await ApplyBeforeAsync(schema.ConnectionString, 2026110946, from: 2026110945);

        Assert.Equal(before, await SnapshotAsync(schema.ConnectionString, tables));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema() AND NOT t.tgisinternal
              AND (t.tgname LIKE '%\_Immutable' OR t.tgname LIKE '%\_NoDelete' OR t.tgname LIKE '%\_Audit' OR t.tgname LIKE '%\_RevokeShares'
                OR t.tgname IN ('TR_UserAccount_History','TR_UserGroup_History','TR_Page_AccessModeHistory','TR_VaultLocation_History',
                    'TR_SecurityConfiguration_History','TR_TrustedAddress_History','TR_HubGroupBinding_History','TR_HubConnectionRevision_Issuer',
                    'TR_HubGroupBinding_Identity','TR_HubIdentityState_Identity','TR_AiMessage_ScrubFeedbackNote'))
            """));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = current_schema() AND (p.proname LIKE 'atlas\_audit\_%' OR p.proname LIKE 'atlas\_revoke\_%' OR p.proname LIKE 'atlas\_record\_%audit'
                OR p.proname LIKE 'atlas\_share\_creator\_%' OR p.proname IN ('atlas_access_audit_immutable','atlas_access_audit_history_guard',
                'atlas_ai_revision_immutable','atlas_access_request_guard','atlas_security_configuration_guard','atlas_trusted_address_guard',
                'atlas_ai_feedback_scrub_note','atlas_version_user_account','atlas_version_user_group','atlas_version_page_access_mode',
                'atlas_version_vault_location','atlas_version_security_configuration','atlas_version_trusted_address','atlas_version_hub_group_binding'))
            """));
    }

    /// <summary>The history and audit rows as text, in a stable order; with <paramref name="history"/> only the rows nothing but a trigger
    /// wrote (the revisions and the audit), not the rows a bare write changes itself.</summary>
    private static async Task<string> SnapshotAsync(string connectionString, bool history = false)
        => await SnapshotAsync(connectionString, history
            ? ["UserAccountRevision", "UserGroupRevision", "PageAccessModeRevision", "AccessAuditEvent", "PageAccessEvent"]
            : ["UserAccountRevision", "UserGroupRevision", "PageAccessModeRevision", "AccessAuditEvent", "PageAccessEvent", "GroupMember",
                "SpaceGrant", "ResourceGrant", "PageShareLink"]);

    private static async Task<string> SnapshotAsync(string connectionString, string[] tables)
    {
        var parts = new List<string>();
        foreach (var table in tables)
        {
            parts.Add(table + ":" + await ScalarAsync(connectionString,
                $"""SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]')::text FROM "{table}" t"""));
        }
        return string.Join('\n', parts);
    }
}
