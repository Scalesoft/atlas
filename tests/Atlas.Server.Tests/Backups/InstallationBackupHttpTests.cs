using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.Server.Features.Backups;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Backups;

/// <summary>The admin endpoints of format v7 through the real application: the key, a complete ZIP and its restore elsewhere.</summary>
public sealed class InstallationBackupHttpTests
{
    [PostgreSqlFact]
    public async Task The_key_is_shown_once_to_an_administrator_and_a_complete_archive_restores_a_new_installation()
    {
        await using var source = await AtlasHttpSite.CreateIsolatedAsync("atlas_complete_source_"); // own database: issues the installation-wide backup key and saves backup settings
        using var admin = source.Client();
        await source.CompleteSetupAsync(admin);
        // Issuing the key is a backup destination change: it needs a fresh sign-in (SEC-T-3).
        var created = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/backups/key", null,
            await AtlasHttpSite.ReauthAsync(admin, source.Password)), HttpStatusCode.OK);
        var key = created["key"]!.GetValue<string>();
        var status = await admin.GetStringAsync("/api/admin/backups/key");
        Assert.Contains(created["keyId"]!.GetValue<string>(), status);
        Assert.DoesNotContain(key, status);

        var destination = Path.Combine(source.DataPath, "complete-backups");
        // A new folder is a security change (SEC1-2).
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/backups",
            new BackupSettingsRequest(false, 1440, "complete", destination, "", "", 10), await AtlasHttpSite.ReauthAsync(admin, source.Password)), HttpStatusCode.OK);
        var backedUp = await BackupHttpTests.BackUpNowAsync(source, admin);
        Assert.Equal("succeeded", backedUp["lastRun"]!["state"]!.GetValue<string>());
        var run = backedUp["lastBackup"]!;
        var zip = await admin.GetByteArrayAsync($"/api/admin/backups/runs/{run["id"]!.GetValue<string>()}/download");

        await using var target = await AtlasHttpSite.CreateIsolatedAsync("atlas_complete_target_"); // own database: a restore replaces the whole installation and needs a new, empty one
        using var restorer = target.Client();
        await target.CompleteSetupAsync(restorer, "New installation");
        // A new installation has only the starter space of the wizard: no content, so it may be restored into.
        var preview = await AtlasHttpSite.Json(await UploadAsync(restorer, "/api/admin/backups/restore/preview", zip, key, false), HttpStatusCode.OK);
        Assert.Equal("full", preview["mode"]!.GetValue<string>());
        Assert.Empty(preview["problems"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(restorer, "/api/admin/backups/restore", zip, key, false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(restorer, "/api/admin/backups/restore", zip, "not a key", true)).StatusCode);

        var restored = await AtlasHttpSite.Json(await UploadAsync(restorer, "/api/admin/backups/restore", zip, key, true), HttpStatusCode.OK);

        Assert.Equal("full", restored["mode"]!.GetValue<string>());
        Assert.Equal("Atlas tests", await target.ScalarAsync("SELECT \"Name\" FROM \"Workspace\""));
        // The safety archive of the installation it replaced was taken first.
        Assert.Equal("complete", await target.ScalarAsync("SELECT \"Mode\" FROM \"BackupRun\" WHERE \"Status\" = 'succeeded'"));
    }

    /// <summary>A hand-written body with the unescaped \ of a Windows path answered a bare "Požadavek není platný."; it names the field now.
    /// The same folder as valid JSON is saved, the ZIP backup runs into it and its archive restores the space elsewhere.</summary>
    [PostgreSqlFact]
    public async Task A_local_folder_destination_takes_a_zip_backup_that_restores_its_space_and_a_malformed_body_names_its_field()
    {
        await using var source = await AtlasHttpSite.CreateIsolatedAsync("atlas_zip_folder_source_"); // own database: saves the installation-wide backup settings
        using var admin = source.Client();
        await source.CompleteSetupAsync(admin);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/spaces", new { name = "Zálohovaný prostor" }), HttpStatusCode.OK);
        var proof = await AtlasHttpSite.ReauthAsync(admin, source.Password);
        var folder = Path.Combine(source.DataPath, "zip");

        using var csrf = JsonDocument.Parse(await admin.GetStringAsync("/api/auth/csrf"));
        using var malformed = new HttpRequestMessage(HttpMethod.Put, "/api/admin/backups")
        {
            Content = new StringContent("""{"enabled":true,"intervalMinutes":1440,"mode":"complete","destination":"C:\Data\zip","username":"","domain":"","retentionCount":10}""",
                System.Text.Encoding.UTF8, "application/json")
        };
        malformed.Headers.Add("X-CSRF-TOKEN", csrf.RootElement.GetProperty("token").GetString());
        malformed.Headers.Add("X-Atlas-Reauth", proof);
        var refused = await AtlasHttpSite.Json(await admin.SendAsync(malformed), HttpStatusCode.BadRequest);
        Assert.Equal("destination", refused["field"]!.GetValue<string>());
        Assert.Equal("invalid_argument", refused["code"]!.GetValue<string>());

        var saved = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/backups",
            new BackupSettingsRequest(true, 1440, "complete", folder, "", "", 10), proof), HttpStatusCode.OK);
        var backedUp = await BackupHttpTests.BackUpNowAsync(source, admin);
        Assert.Equal("succeeded", backedUp["lastRun"]!["state"]!.GetValue<string>());
        var zip = await File.ReadAllBytesAsync(Path.Combine(folder, backedUp["lastBackup"]!["fileName"]!.GetValue<string>()));

        await using var target = await AtlasHttpSite.CreateIsolatedAsync("atlas_zip_folder_target_"); // own database: a restore replaces the whole installation and needs a new, empty one
        using var restorer = target.Client();
        await target.CompleteSetupAsync(restorer, "New installation");
        await AtlasHttpSite.Json(await UploadAsync(restorer, "/api/admin/backups/restore", zip, saved["newBackupKey"]!["key"]!.GetValue<string>(), true),
            HttpStatusCode.OK);

        Assert.Equal(1L, await target.ScalarAsync("SELECT count(*) FROM \"Space\" WHERE \"Name\" = 'Zálohovaný prostor'"));
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string path, byte[] zip, string key, bool acknowledge)
    {
        using var csrf = JsonDocument.Parse(await client.GetStringAsync("/api/auth/csrf"));
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(zip), "file", "complete.zip");
        form.Add(new StringContent(key), "key");
        form.Add(new StringContent(acknowledge ? "true" : "false"), "acknowledge");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.RootElement.GetProperty("token").GetString());
        return await client.SendAsync(request);
    }
}
