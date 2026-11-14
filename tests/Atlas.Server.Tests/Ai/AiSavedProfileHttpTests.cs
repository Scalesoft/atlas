using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Ai;

public sealed class AiSavedProfileHttpTests
{
    [PostgreSqlFact]
    public async Task Library_routes_bind_versions_and_keep_credentials_write_only()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_saved_ai_http_", sharedAs: "ai-saved-profiles"); // a site of this class: its connection test calls the fake model, and tests on the default site assert that nothing called it
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        const string endpoint = "/api/admin/ai/profiles/chat/saved";
        const string key = "saved-http-fixture-credential";
        var saved = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, endpoint,
            new { name = "Offline " + Guid.NewGuid().ToString("N"), profile = AiSettingsFixture.Chat(key), expectedVersion = (string?)null }), HttpStatusCode.OK);
        var id = saved["id"]!.GetValue<string>();
        var version = saved["version"]!.GetValue<string>();
        Assert.Null(saved["profile"]!.AsObject()["apiKey"]);
        Assert.Null(saved["profile"]!.AsObject()["protectedApiKey"]);
        Assert.DoesNotContain(key, saved.ToJsonString(), StringComparison.Ordinal);
        // Saving does not activate (the list's activeVersion belongs to the whole installation, so the profile itself says it).
        Assert.False(saved["isActive"]!.GetValue<bool>());
        var list = await AtlasHttpSite.Json(await admin.GetAsync(endpoint), HttpStatusCode.OK);
        Assert.Single(list["items"]!.AsArray(), item => item!["id"]!.GetValue<string>() == id);
        var loaded = await AtlasHttpSite.Json(await admin.GetAsync(endpoint + "/" + id), HttpStatusCode.OK);
        Assert.Equal(version, loaded["profile"]!["version"]!.GetValue<string>());
        var tested = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, endpoint + "/" + id + "/test",
            new { expectedVersion = version }), HttpStatusCode.OK);
        Assert.True(tested["ok"]!.GetValue<bool>());
        var models = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, endpoint + "/" + id + "/models",
            new { provider = "openai-compatible", location = "cloud", baseUrl = "https://api.openai.example/v1",
                apiKey = (string?)null, clearApiKey = false, expectedSavedVersion = version }), HttpStatusCode.OK);
        Assert.True(models["ok"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Conflict, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Delete, endpoint + "/" + id)).StatusCode);
        var removed = await AtlasHttpSite.SendAsync(admin, HttpMethod.Delete, endpoint + "/" + id + "?expectedVersion=" + Uri.EscapeDataString(version));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(endpoint + "/" + id)).StatusCode);
        Assert.DoesNotContain(site.Logs.Messages, line => line.Contains(key, StringComparison.Ordinal));
    }
}
