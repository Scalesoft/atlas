using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Events.OutboxTests;

namespace Atlas.Server.Tests.Security;

/// <summary>SEC-A-1, SEC-T-3: saving the OIDC providers and issuing a backup key are security changes with an audit event of their own
/// (migration 2026110160).</summary>
public sealed class SecuritySettingsAuditTests
{
    private const string Admin = "00000000-0000-0000-0000-000000000001";

    /// <summary>Over an existing installation: the audit keeps its events, no actor is invented for the OIDC settings saved before, and a
    /// revision saved afterwards and a backup key each leave one event.</summary>
    [PostgreSqlFact]
    public async Task Existing_settings_stay_unaudited_and_new_ones_are_audited()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_security_settings_audit_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026110160);
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Admin}','admin','Admin',true,true,'stamp');
            INSERT INTO "OidcSettingsRevision"("Id","PublicOrigin","AuthorId","ActiveFrom","ActiveTo")
              VALUES('00000000-0000-0000-0000-000000000011','https://wiki.example','{Admin}','2026-01-01T00:00:00Z','2026-02-01T00:00:00Z');
            INSERT INTO "BackupDestinationChange"("ActorId","Kind","Destination") VALUES('{Admin}','git.remote','git@example.test:a.git');
            """);
        const string Events = """SELECT string_agg("Action", ',' ORDER BY "Id") FROM "AccessAuditEvent" """;
        var before = await ScalarAsync(schema.ConnectionString, Events);

        // Up to the migrations that move this audit into the code (2026110942, 2026110944; tested in AuditInCodeMigrationTests).
        await ApplyBeforeAsync(schema.ConnectionString, 2026110941, from: 2026110160);

        Assert.Equal(before, await ScalarAsync(schema.ConnectionString, Events));
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "OidcSettingsRevision"("Id","PublicOrigin","AuthorId","ActiveFrom") VALUES('00000000-0000-0000-0000-000000000012','https://wiki.example','{Admin}','2026-02-01T00:00:00Z');
            INSERT INTO "BackupDestinationChange"("ActorId","Kind","Destination") VALUES('{Admin}','backup.key','key-1');
            """);
        Assert.Equal($"oidc.configuration_changed|{Admin}|00000000-0000-0000-0000-000000000012", await ScalarAsync(schema.ConnectionString, """
            SELECT "Action" || '|' || "ActorId" || '|' || "OidcSettingsRevisionId" FROM "AccessAuditEvent" WHERE "OidcSettingsRevisionId" IS NOT NULL
            """));
        Assert.Equal("backup.destination_changed|backup.key|key-1", await ScalarAsync(schema.ConnectionString, """
            SELECT "Action" || '|' || "BackupKind" || '|' || "BackupDestination" FROM "AccessAuditView" WHERE "BackupKind" = 'backup.key'
            """));
    }

    [PostgreSqlFact]
    public async Task A_backup_key_needs_a_fresh_sign_in_and_is_audited_without_the_key()
    {
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_backup_key_reauth_") /* own database: the backup key is one row (BackupSettings) for the whole installation; the test starts without one */;
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);

        var refused = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/backups/key");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Null((await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/backups/key"), HttpStatusCode.OK))["keyId"]);
        var created = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/backups/key", null,
            await AtlasHttpSite.ReauthAsync(admin, site.Password)), HttpStatusCode.OK);

        var audit = await admin.GetStringAsync("/api/admin/access-audit?limit=100");
        Assert.Contains("\"backupKind\":\"backup.key\"", audit);
        Assert.Contains(created["keyId"]!.GetValue<string>(), audit);
        Assert.DoesNotContain(created["key"]!.GetValue<string>(), audit);
    }

    /// <summary>Enabling Git over a repository saved before creates the missing backup key and shows it once, so that save needs the
    /// fresh sign-in although the repository stays, and the key is audited as backup.key with its ID.</summary>
    [PostgreSqlFact]
    public async Task Enabling_git_over_a_saved_repository_issues_the_backup_key_only_after_a_fresh_sign_in()
    {
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_backup_git_key_") /* own database: it rewrites GitBackupSettings and BackupSettings, one row each for the whole installation */;
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        const string remote = "git@git.example.com:someone/copy.git";
        await site.ExecuteAsync("""UPDATE "GitBackupSettings" SET "RemoteUrl" = @remote, "PublicKey" = 'ssh-ed25519 AAAAC3Nza test', "Enabled" = false""",
            ("remote", remote));
        var enable = new { enabled = true, remoteUrl = remote, branch = "main" };

        var refused = await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/backups/git", enable);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("reauth_required", await refused.Content.ReadAsStringAsync());
        Assert.Equal(DBNull.Value, await site.ScalarAsync("""SELECT "ProtectedBackupKey" FROM "BackupSettings" """));

        var saved = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/backups/git", enable,
            await AtlasHttpSite.ReauthAsync(admin, site.Password)), HttpStatusCode.OK);
        Assert.Equal($"backup.key|{saved["newBackupKey"]!["keyId"]!.GetValue<string>()}", await site.ScalarAsync("""
            SELECT string_agg(c."Kind" || '|' || c."Destination", ',') FROM "BackupDestinationChange" c
            JOIN "AccessAuditEvent" e ON e."BackupDestinationChangeId" = c."Id" AND e."ActorId" = c."ActorId"
            """));
    }
}
