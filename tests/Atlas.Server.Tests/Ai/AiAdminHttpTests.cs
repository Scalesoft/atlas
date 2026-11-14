using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Ai;

public sealed class AiAdminHttpTests
{
    private const string SyntheticKey = "ai-http-fixture-credential";

    [PostgreSqlFact]
    public async Task Model_catalogue_accepts_an_unsaved_connection_without_model_or_dimensions()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_ai_models_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        // The embeddings profile is the whole installation's: an address of this test's own shows the unsaved connection was not saved.
        var baseUrl = $"http://m{Guid.NewGuid():N}.models.example/v1";
        var result = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/ai/profiles/embeddings/models",
            new { provider = "openai-compatible", location = "company", baseUrl, apiKey = SyntheticKey, clearApiKey = false }), HttpStatusCode.OK);
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal("fake-trigram-1024", result["models"]![0]!["id"]!.GetValue<string>());
        Assert.DoesNotContain(SyntheticKey, result.ToJsonString(), StringComparison.Ordinal);
        var profile = await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/ai/profiles/embeddings"), HttpStatusCode.OK);
        Assert.NotEqual(baseUrl, profile["baseUrl"]?.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task Profile_endpoint_never_serializes_the_key_and_binds_kind_and_body()
    {
        // Own site: saves the installation's chat provider (AiProfileAssignment/AiProfileRevision, one per kind) from no version.
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_ai_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        const string endpoint = "/api/admin/ai/profiles/chat";
        var body = new
        {
            enabled = true, provider = "anthropic", location = "cloud", baseUrl = (string?)null, model = "claude-test",
            timeoutSeconds = 60, maxConcurrency = 2, reasoningEffort = "low", nativeJsonSchema = true, apiKey = SyntheticKey,
            clearApiKey = false, expectedVersion = (string?)null,
        };
        var put = await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, endpoint, body);
        var putText = await put.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await AtlasHttpSite.Json(await admin.GetAsync(endpoint), HttpStatusCode.OK);
        Assert.Equal(("chat", "anthropic", "claude-test", true), (saved["kind"]!.GetValue<string>(), saved["provider"]!.GetValue<string>(),
            saved["model"]!.GetValue<string>(), saved["hasApiKey"]!.GetValue<bool>()));
        Assert.Null(saved.AsObject()["apiKey"]);
        Assert.DoesNotContain(SyntheticKey, putText, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticKey, saved.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/ai/profiles/other")).StatusCode);
        // "Test spojení" answers the fake provider's outcome as codes (A2); the site's factory is the fake one.
        var tested = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, endpoint + "/test"), HttpStatusCode.OK);
        Assert.Equal((true, "fake-chat"), (tested["ok"]!.GetValue<bool>(), tested["model"]!.GetValue<string>()));
        Assert.DoesNotContain(SyntheticKey, tested.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(site.Logs.Messages, line => line.Contains(SyntheticKey, StringComparison.Ordinal));

        // A3: the overview is served at the module's root (the master switch is still off, so the enabled profile is Disabled),
        // and the connection test is limited to 6 a minute per admin.
        var overview = await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/ai"), HttpStatusCode.OK);
        Assert.Equal(("chat", "Disabled", "claude-test"), (overview["profiles"]![0]!["kind"]!.GetValue<string>(),
            overview["profiles"]![0]!["state"]!.GetValue<string>(), overview["profiles"]![0]!["model"]!.GetValue<string>()));
        Assert.DoesNotContain(SyntheticKey, overview.ToJsonString(), StringComparison.Ordinal);
        for (var i = 1; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, endpoint + "/test")).StatusCode);
        }
        var refused = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, endpoint + "/test");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        // The global rejection callback (AtlasServices OnRejected) tells the client when to retry, in whole seconds.
        Assert.Matches("^[1-9][0-9]*$", Assert.Single(refused.Headers.GetValues("Retry-After")));
    }
}
