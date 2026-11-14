using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class ModuleSettingsHttpTests
{
    [PostgreSqlFact]
    public async Task New_setup_resumes_module_steps_and_completion_is_authenticated_and_idempotent()
    {
        // A site of its own: the test is about the installation itself (its setup and the module steps it resumes, ModuleOnboarding).
        await using var site = await AtlasHttpSite.CreateAsync("atlas_modules_", settings => settings["YouTrack:Enabled"] = "true"); using var admin = site.Client(); using var anonymous = site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/modules")).StatusCode);
        await Setup(site, admin);
        using var state = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/setup"));
        Assert.True(state.RootElement.GetProperty("pending").GetBoolean());
        using var modules = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/modules"));
        var ids = modules.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(["oidc", "youtrack", "ai", "editor-assist", "mcp", "hub", "wiki-js-import"], ids);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/setup/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(admin, HttpMethod.Post, "/api/admin/setup/complete")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(admin, HttpMethod.Post, "/api/admin/setup/complete")).StatusCode);
        using var complete = JsonDocument.Parse(await admin.GetStringAsync("/api/admin/setup"));
        Assert.False(complete.RootElement.GetProperty("pending").GetBoolean());
    }

    [PostgreSqlFact]
    public async Task Oidc_settings_are_encrypted_temporal_compare_and_swap_and_require_wiki_admin()
    {
        // A site of its own: it installs the wiki and saves its sign-in settings (OidcSettingsRevision, a row of the whole installation).
        await using var site = await AtlasHttpSite.CreateAsync("atlas_modules_", settings => settings["YouTrack:Enabled"] = "true"); using var admin = site.Client(); await Setup(site, admin);
        const string endpoint = "/api/admin/modules/oidc";
        var initial = (await admin.GetFromJsonAsync<OidcSettingsView>(endpoint))!;
        Assert.Null(initial.Version); Assert.Empty(initial.Providers);
        var body = new SaveOidcSettings(null, "https://wiki.example", [new("hub", "Hub", "https://hub.example", "client", "fixture-only-secret", false, ["openid", "profile"], false)]);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(endpoint, body)).StatusCode);
        // SEC-A-1: who may sign in, and who becomes a wiki administrator through it, is a security change.
        var refused = await Send(admin, HttpMethod.Put, endpoint, body); Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("reauth_required", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var response = await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, endpoint, body, await AtlasHttpSite.ReauthAsync(admin, site.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("fixture-only-secret", text);
        var saved = JsonSerializer.Deserialize<OidcSettingsView>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(saved.RestartRequired); Assert.True(Assert.Single(saved.Providers).HasSecret);
        Assert.Equal("https://wiki.example/signin-oidc/hub", saved.Providers[0].CallbackUrl);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(admin, HttpMethod.Put, endpoint, body)).StatusCode);
        var preserved = body with { ExpectedVersion = saved.Version, Providers = [body.Providers[0] with { ClientSecret = null, DisplayName = "Corporate account" }] };
        var updatedResponse = await Send(admin, HttpMethod.Put, endpoint, preserved); Assert.Equal(HttpStatusCode.OK, updatedResponse.StatusCode);
        var updated = (await updatedResponse.Content.ReadFromJsonAsync<OidcSettingsView>())!;
        Assert.True(updated.Providers[0].HasSecret);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(admin, HttpMethod.Put, endpoint, preserved with { ExpectedVersion = updated.Version, Providers = [preserved.Providers[0] with { Issuer = "https://other.example" }] })).StatusCode);
        await using (var sql = new NpgsqlConnection(site.ConnectionString))
        {
            await sql.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT r.\"ActiveFrom\",r.\"ActiveTo\",p.\"ProtectedSecret\" FROM \"OidcSettingsRevision\" r JOIN \"OidcProviderSettings\" p ON p.\"SettingsId\"=r.\"Id\" ORDER BY r.\"ActiveFrom\"", sql);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); var boundary = reader.GetDateTime(1); Assert.DoesNotContain("fixture-only-secret", reader.GetString(2));
            Assert.True(await reader.ReadAsync()); Assert.Equal(boundary, reader.GetDateTime(0)); Assert.True(reader.IsDBNull(1)); Assert.False(await reader.ReadAsync());
        }
        await using (var sql = new NpgsqlConnection(site.ConnectionString))
        {
            await sql.OpenAsync();
            await using var audited = new NpgsqlCommand("SELECT COUNT(*) FROM \"AccessAuditEvent\" e JOIN \"OidcSettingsRevision\" r ON r.\"Id\"=e.\"OidcSettingsRevisionId\" WHERE e.\"Action\"='oidc.configuration_changed' AND e.\"ActorId\"=r.\"AuthorId\"", sql);
            Assert.Equal(2L, await audited.ExecuteScalarAsync());
        }
        // Server startup still has no OIDC registration until the restart boundary.
        using var methods = JsonDocument.Parse(await admin.GetStringAsync("/api/auth/methods")); Assert.Equal(0, methods.RootElement.GetProperty("providers").GetArrayLength());
        var created = await Send(admin, HttpMethod.Post, "/api/admin/users", new { username = "reader", displayName = "Reader" });
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync()); using var readerClient = site.Client();
        await Send(readerClient, HttpMethod.Post, "/api/auth/activate", new { token = createdJson.RootElement.GetProperty("token").GetString(), newPassword = site.Password });
        await Send(readerClient, HttpMethod.Post, "/api/auth/login", new { username = "reader", password = site.Password });
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.GetAsync(endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(readerClient, HttpMethod.Put, endpoint, preserved with { ExpectedVersion = updated.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(readerClient, HttpMethod.Post, "/api/admin/setup/complete")).StatusCode);
    }
    private static async Task Setup(AtlasHttpSite site, HttpClient client)
    {
        var response = await Send(client, HttpMethod.Post, "/api/setup/complete", new { bootstrapToken = site.BootstrapToken, workspaceName = "Module fixture", username = "admin", displayName = "Admin", password = site.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? body = null) => AtlasHttpSite.SendAsync(client, method, path, body);
}
