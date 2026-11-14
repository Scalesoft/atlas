using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Backups;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Backups;

public sealed class BackupHttpTests
{
    /// <summary>„Zálohovat teď“ and its run: the site runs no job queue (a requested run stays queued for IJobRunner), so this runs the queued
    /// run as the queue would, then reads it as the page does (<c>GET /run</c>). The page's state after it, the new archive as lastBackup and
    /// the run as lastRun.</summary>
    internal static async Task<JsonNode> BackUpNowAsync(AtlasHttpSite site, HttpClient admin)
    {
        var runId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/backups/run"), HttpStatusCode.OK))["runId"]!.GetValue<long>();
        Assert.Equal(runId, await site.Services.GetRequiredService<IJobRunner>().RunToEndAsync(ZipBackupJob.JobId, "Zálohovat teď"));
        var run = await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/backups/run"), HttpStatusCode.OK);
        Assert.Equal(runId, run["id"]!.GetValue<long>());
        var status = await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/backups"), HttpStatusCode.OK);
        status["lastRun"] = run.DeepClone();
        return status;
    }

    [PostgreSqlFact]
    public async Task Malformed_archive_is_rejected_as_user_error_before_import()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_backup_badzip_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        using var csrf = JsonDocument.Parse(await admin.GetStringAsync("/api/auth/csrf"));
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "bad.zip");
        form.Add(new StringContent("add"), "mode");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/backups/import/preview") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.RootElement.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(request)).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Backup_settings_require_admin_and_csrf_and_show_unconfigured_warning()
    {
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_backups_http_"); // own database: asserts the unconfigured backup settings and saves them (one row for the installation)
        using var admin = site.Client();
        await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/setup/complete", new
        {
            bootstrapToken = site.BootstrapToken, workspaceName = "Backup fixture", username = "admin",
            displayName = "Admin", password = site.Password
        });
        const string endpoint = "/api/admin/backups";
        var response = await admin.GetAsync(endpoint);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = (await response.Content.ReadFromJsonAsync<BackupStatus>())!;
        Assert.NotNull(status.Warning);
        Assert.False(status.Settings.Enabled);
        // No run of the ZIP job yet: the page shows none, without an error.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.GetAsync(endpoint + "/run")).StatusCode);
        var request = new BackupSettingsRequest(false, 1440, "full", "", "", "", 10);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(endpoint, request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, endpoint, request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, endpoint,
            request with { Destination = "relative/folder" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, endpoint,
            request with { Destination = "smb://server" })).StatusCode);

        var created = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users", new { username = "reader", displayName = "Reader" });
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        using var member = site.Client();
        await AtlasHttpSite.SendAsync(member, HttpMethod.Post, "/api/auth/activate", new { token = body.RootElement.GetProperty("token").GetString(), newPassword = site.Password });
        await AtlasHttpSite.SendAsync(member, HttpMethod.Post, "/api/auth/login", new { username = "reader", password = site.Password });
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(member, HttpMethod.Post, endpoint + "/run")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(endpoint + "/run")).StatusCode);
    }

    /// <summary>SEC1-2: a backup holds the private notes and personal boards, so pointing it at another repository, folder or key needs
    /// a fresh sign-in and leaves an access audit event with the new destination; saving the same destination again asks nothing.</summary>
    [PostgreSqlFact]
    public async Task Changing_a_backup_destination_needs_a_fresh_sign_in_and_is_audited()
    {
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_backup_destination_"); // own database: backup settings, GitBackupSettings and BackupDestinationChange are installation-wide rows counted by the test
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        const string git = "/api/admin/backups/git";
        var elsewhere = new { enabled = false, remoteUrl = "git@git.example.com:someone/copy.git", branch = "main" };

        var refused = await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, git, elsewhere);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("reauth_required", await refused.Content.ReadAsStringAsync());
        Assert.Equal("", await Tests.Events.OutboxTests.ScalarAsync(site.ConnectionString, """SELECT "RemoteUrl" FROM "GitBackupSettings" """));
        var archive = new BackupSettingsRequest(false, 1440, "full", Path.Combine(site.DataPath, "elsewhere"), "", "", 10);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/backups", archive)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, git + "/key")).StatusCode);
        Assert.Equal(0L, await Events(site));

        var proof = await AtlasHttpSite.ReauthAsync(admin, site.Password);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, git, elsewhere, proof)).StatusCode);
        // The same destination again is no change: nothing to sign in for, nothing audited.
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, git, elsewhere)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/backups", archive)).StatusCode);
        Assert.Equal(2L, await Events(site));
        Assert.Equal($"archive.path|{archive.Destination},git.remote|{elsewhere.remoteUrl}", await Tests.Events.OutboxTests.ScalarAsync(site.ConnectionString, """
            SELECT string_agg(c."Kind" || '|' || c."Destination", ',' ORDER BY c."Kind") FROM "BackupDestinationChange" c
            JOIN "AccessAuditEvent" e ON e."BackupDestinationChangeId" = c."Id" AND e."ActorId" = c."ActorId"
            """));
    }

    /// <summary>SEC3-1, SEC3-4: the switch to the complete mode needs the fresh sign-in although the folder stays, a complete archive
    /// goes out only after one and is audited as backup.downloaded, and the audit view names what each change points at. A save the
    /// server refuses after the sign-in leaves no destination row.</summary>
    [PostgreSqlFact]
    public async Task The_complete_mode_and_its_download_need_a_fresh_sign_in_and_name_their_destination_in_the_audit()
    {
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_backup_complete_"); // own database: backup settings and the backup key are installation-wide; the audit view is read whole
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        const string endpoint = "/api/admin/backups";
        var folder = Path.Combine(site.DataPath, "zip");
        var full = new BackupSettingsRequest(true, 1440, "full", folder, "", "", 10);
        await AtlasHttpSite.ReauthAsync(admin, site.Password);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, endpoint, full)).StatusCode);
        var fullRun = (await BackUpNowAsync(site, admin))["lastBackup"]!;

        // Another session of the same administrator has no window: the same folder in the complete mode is refused.
        using var later = await SignInAsync(site);
        var refused = await AtlasHttpSite.SendAsync(later, HttpMethod.Put, endpoint, full with { Mode = "complete" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("reauth_required", await refused.Content.ReadAsStringAsync());
        // A full archive goes out as before; a refused save (a missing profile) writes no destination row.
        Assert.Equal(HttpStatusCode.OK, (await later.GetAsync($"{endpoint}/runs/{fullRun["id"]}/download")).StatusCode);
        var proof = await AtlasHttpSite.ReauthAsync(later, site.Password);
        var rows = await Rows(site);
        Assert.NotEqual(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(later, HttpMethod.Put, endpoint, full with { StorageProfileId = Guid.NewGuid() }, proof)).StatusCode);
        Assert.Equal(rows, await Rows(site));
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(later, HttpMethod.Put, endpoint, full with { Mode = "complete" })).StatusCode);
        var completeStatus = await BackUpNowAsync(site, later);
        Assert.Equal("succeeded", completeStatus["lastRun"]!["state"]!.GetValue<string>());
        var completeRun = completeStatus["lastBackup"]!;
        Assert.Equal("complete", completeRun["mode"]!.GetValue<string>());

        using var third = await SignInAsync(site);
        var download = $"{endpoint}/runs/{completeRun["id"]}/download";
        Assert.Equal(HttpStatusCode.Forbidden, (await third.GetAsync(download)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(third, HttpMethod.Post, download + "/authorize")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await AtlasHttpSite.SendAsync(third, HttpMethod.Post, download + "/authorize", null, await AtlasHttpSite.ReauthAsync(third, site.Password))).StatusCode);
        var file = await third.GetAsync(download);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/zip", file.Content.Headers.ContentType?.MediaType);

        // The save of the complete mode created the backup key, a destination change of its own named by the key's ID (0af749df).
        var keyId = (await AtlasHttpSite.Json(await third.GetAsync($"{endpoint}/key"), HttpStatusCode.OK))["keyId"]!.GetValue<string>();
        Assert.Equal($"backup.destination_changed|archive.path|{folder};backup.destination_changed|archive.complete|{folder};"
            + $"backup.destination_changed|backup.key|{keyId};backup.downloaded|archive.download|{completeRun["fileName"]}", await Tests.Events.OutboxTests.ScalarAsync(site.ConnectionString, """
            SELECT string_agg("Action" || '|' || "BackupKind" || '|' || "BackupDestination", ';' ORDER BY "OccurredAt", "Id") FROM "AccessAuditView"
            WHERE "BackupDestinationChangeId" IS NOT NULL
            """));
    }

    /// <summary>SEC3-3: the ZIP backups follow their storage profile, so a new root of that profile needs a fresh sign-in and is
    /// audited with the folder the backups now reach; a profile the backups do not use, or a rename, asks nothing.</summary>
    [PostgreSqlFact]
    public async Task Editing_the_storage_profile_of_the_backups_is_a_destination_change()
    {
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_backup_profile_"); // own database: backup settings (one row) and the destination-change table read as "the last row"
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var root = Path.Combine(site.DataPath, "profile");
        object Profile(string name, string destination, int? version = null) => new
        {
            name, destination, username = "", domain = "", enabled = true, allowPageExports = false, allowBackups = true, allSpaces = true,
            spaceIds = Array.Empty<Guid>(), expectedVersion = version,
        };
        var used = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/storage-profiles", Profile("Zálohy", root)), HttpStatusCode.OK))["id"]!
            .GetValue<Guid>();
        var unused = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/storage-profiles", Profile("Jiné", Path.Combine(site.DataPath, "other"))),
            HttpStatusCode.OK))["id"]!.GetValue<Guid>();
        await AtlasHttpSite.ReauthAsync(admin, site.Password);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/backups",
            new BackupSettingsRequest(true, 1440, "full", "", "", "", 10, StorageProfileId: used, Folder: "wiki"))).StatusCode);

        using var later = await SignInAsync(site);
        var moved = Path.Combine(site.DataPath, "moved");
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(later, HttpMethod.Put, $"/api/storage-profiles/{unused}",
            Profile("Jiné", Path.Combine(site.DataPath, "other2"), 1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(later, HttpMethod.Put, $"/api/storage-profiles/{used}", Profile("Zálohy wiki", root, 1))).StatusCode);
        var refused = await AtlasHttpSite.SendAsync(later, HttpMethod.Put, $"/api/storage-profiles/{used}", Profile("Zálohy wiki", moved, 2));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("reauth_required", await refused.Content.ReadAsStringAsync());
        var before = await Rows(site);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(later, HttpMethod.Put, $"/api/storage-profiles/{used}", Profile("Zálohy wiki", moved, 2),
            await AtlasHttpSite.ReauthAsync(later, site.Password))).StatusCode);
        Assert.Equal(before + 1, await Rows(site));
        Assert.Equal($"archive.path|{Path.Combine(moved, "wiki")}", await Tests.Events.OutboxTests.ScalarAsync(site.ConnectionString, """
            SELECT "Kind" || '|' || "Destination" FROM "BackupDestinationChange" ORDER BY "Id" DESC LIMIT 1
            """));
    }

    private static async Task<HttpClient> SignInAsync(AtlasHttpSite site)
    {
        var client = site.Client();
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/login", new { username = "administrator", password = site.Password })).StatusCode);
        return client;
    }

    private static async Task<long> Rows(AtlasHttpSite site) => (long)(await Tests.Events.OutboxTests.ScalarAsync(site.ConnectionString,
        """SELECT count(*) FROM "BackupDestinationChange" """))!;

    private static async Task<long> Events(AtlasHttpSite site) => (long)(await Tests.Events.OutboxTests.ScalarAsync(site.ConnectionString,
        """SELECT count(*) FROM "AccessAuditEvent" WHERE "Action" = 'backup.destination_changed'"""))!;
}