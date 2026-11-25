using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Atlas.Server.Features.AiIndex;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.PersonalApi;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class PersonalApiHttpTests
{
    [PostgreSqlFact]
    public async Task Scalar_is_hosted_by_dotnet_and_uses_the_authenticated_OpenApi_document()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_scalar_");
        using var client = site.Client();
        // The shell, with or without a document name, and the document need a session (checked after the Scalar.AspNetCore 3 upgrade); Scalar serves its own
        // bundled scripts and icon anonymously by design, they hold nothing of the wiki.
        foreach (var path in new[] { "/scalar/", "/scalar/v1", "/openapi/v1.json" })
        {
            Assert.Equal((path, HttpStatusCode.Unauthorized), (path, (await client.GetAsync(path)).StatusCode));
        }
        await site.CompleteSetupAsync(client);
        var response = await client.GetAsync("/scalar/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        // Scalar.AspNetCore bootstraps with an inline script, so the SPA document policy stays off /scalar.
        Assert.False(response.Headers.Contains("Content-Security-Policy-Report-Only"));
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("scalar.aspnetcore.js", html);
        Assert.Contains("openapi/v1.json", html);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/scalar/scalar.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/scalar/scalar.aspnetcore.js")).StatusCode);
        var document = await Json(await client.GetAsync("/openapi/v1.json"), HttpStatusCode.OK);
        Assert.NotNull(document["paths"]!["/api/v1/pages/{id}/publish"]);
        Assert.Null(document["paths"]!["/api/admin/users"]);
        Assert.Equal("bearer", document["components"]!["securitySchemes"]!["PersonalToken"]!["scheme"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task Token_scope_never_elevates_owner_access_and_expiry_is_enforced()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_api_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var readerLogin = TestLogin.For("reader");
        var invitation = await Json(await Send(admin, HttpMethod.Post, "/api/admin/users", new { username = readerLogin, displayName = "Reader" }), HttpStatusCode.Created);
        var userId = invitation["user"]!["id"]!.GetValue<string>();
        using var owner = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(owner, HttpMethod.Post, "/api/auth/activate", new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await Json(await Send(owner, HttpMethod.Post, "/api/auth/login", new { username = readerLogin, password = site.Password }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(owner, HttpMethod.Put, $"/api/admin/users/{userId}/api-access", new { canIssueTokens = true, expectedVersion = (string?)null })).StatusCode);
        await Json(await Send(admin, HttpMethod.Put, $"/api/admin/users/{userId}/api-access", new { canIssueTokens = true, expectedVersion = (string?)null }), HttpStatusCode.OK);
        await AtlasHttpSite.ReauthAsync(owner, site.Password);
        var token = await Json(await Send(owner, HttpMethod.Post, "/api/profile/api-tokens", new { name = "Scoped integration", scopes = new[] { "read", "write" }, expiresInDays = 1 }), HttpStatusCode.Created);
        using var api = site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token["secret"]!.GetValue<string>());
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access", new { userId, role = "Reader", expectedVersion = (string?)null })).StatusCode);
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var page = await Json(await Send(admin, HttpMethod.Post, "/api/pages", new { spaceId, parentId = (string?)null, title = "Reader handbook", contentJson = rich, markdown = "Published API reference" }), HttpStatusCode.OK);
        var pageId = page["id"]!.GetValue<string>();
        await Json(await Send(admin, HttpMethod.Post, $"/api/pages/{pageId}/publish", new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"/api/v1/pages/{pageId}")).StatusCode);
        // The token's owner reads the space, so a root page without Writer is 403, not 404 (review S3).
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsJsonAsync("/api/v1/pages", new { spaceId, parentId = (string?)null, title = "Escalation", markdown = "Forbidden" })).StatusCode);
        Assert.Single((await Json(await api.GetAsync("/api/v1/search?q=handbook"), HttpStatusCode.OK))["items"]!.AsArray());
        var access = await Json(await admin.GetAsync($"/api/spaces/{spaceId}/access"), HttpStatusCode.OK);
        var grant = access["grants"]!.AsArray().Single(g => g!["userId"]?.GetValue<string>() == userId)!;
        Assert.Equal(HttpStatusCode.NoContent, (await Send(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access", new { userId, role = (string?)null, expectedVersion = grant["version"]!.GetValue<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/v1/pages/{pageId}")).StatusCode);
        Assert.Empty((await Json(await api.GetAsync("/api/v1/search?q=handbook"), HttpStatusCode.OK))["items"]!.AsArray());
        await ExpireAsync(site, Guid.Parse(token["token"]!["id"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/spaces")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Personal_tokens_require_explicit_permission_enforce_scope_and_revoke_without_resurrection()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_api_");
        using var owner = site.Client();
        var setup = await site.CompleteSetupAsync(owner, "Personal API acceptance", "api-owner", "API Owner");
        var ownerId = setup["id"]!.GetValue<string>();
        var profile = await Json(await owner.GetAsync("/api/profile/api-tokens"), HttpStatusCode.OK);
        Assert.False(profile["canIssueTokens"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(owner, HttpMethod.Post, "/api/profile/api-tokens", new { name = "Denied", scopes = new[] { "read" }, expiresInDays = 30 })).StatusCode);
        var permission = await Json(await Send(owner, HttpMethod.Put, $"/api/admin/users/{ownerId}/api-access", new { canIssueTokens = true, expectedVersion = (string?)null }), HttpStatusCode.OK);
        // SEC-A-2: a token outlives the session that issued it, so issuing one needs a fresh sign-in.
        var unconfirmed = await Send(owner, HttpMethod.Post, "/api/profile/api-tokens", new { name = "Unconfirmed", scopes = new[] { "read" }, expiresInDays = 30 });
        Assert.Equal((HttpStatusCode.Forbidden, "reauth_required"), (unconfirmed.StatusCode, (await unconfirmed.Content.ReadFromJsonAsync<JsonNode>())!["code"]!.GetValue<string>()));
        await AtlasHttpSite.ReauthAsync(owner, site.Password);
        var readToken = await Json(await Send(owner, HttpMethod.Post, "/api/profile/api-tokens", new { name = "Read integration", scopes = new[] { "read" }, expiresInDays = 30 }), HttpStatusCode.Created);
        var secret = readToken["secret"]!.GetValue<string>();
        using var reader = site.Client();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/spaces")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/v1/spaces")).StatusCode); // Cookie is insufficient.
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/api/admin/users")).StatusCode); // PAT is never a session.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/pages", new { })).StatusCode);
        var document = await Json(await reader.GetAsync("/api/v1/openapi.json"), HttpStatusCode.OK);
        Assert.NotNull(document["paths"]!["/api/v1/pages/{id}"]);
        Assert.NotNull(document["paths"]!["/api/v1/me/library/{kind}"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]);
        Assert.Equal("SetPersonalFavorite",document["paths"]!["/api/v1/pages/{id}/favorite"]!["put"]!["operationId"]!.GetValue<string>());
        // The scope note follows the policy each operation really requires.
        Assert.EndsWith("Requires write scope.", document["paths"]!["/api/v1/pages/{id}/favorite"]!["put"]!["description"]!.GetValue<string>());
        Assert.EndsWith("Requires write scope.", document["paths"]!["/api/v1/pages/{id}/trash"]!["post"]!["description"]!.GetValue<string>());
        Assert.Null(document["paths"]!["/api/v1/spaces"]!["get"]!["description"]);
        Assert.Null(document["paths"]!["/api/admin/users"]);
        var profileJson = await (await owner.GetAsync("/api/profile/api-tokens")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, profileJson);

        permission = await Json(await Send(owner, HttpMethod.Put, $"/api/admin/users/{ownerId}/api-access", new { canIssueTokens = false, expectedVersion = permission["version"]!.GetValue<string>() }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/api/v1/spaces")).StatusCode);
        await Json(await Send(owner, HttpMethod.Put, $"/api/admin/users/{ownerId}/api-access", new { canIssueTokens = true, expectedVersion = permission["version"]!.GetValue<string>() }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/api/v1/spaces")).StatusCode); // Re-enable never revives old tokens.

        var writerToken = await Json(await Send(owner, HttpMethod.Post, "/api/profile/api-tokens", new { name = "Writer integration", scopes = new[] { "read", "write" }, expiresInDays = 7 }), HttpStatusCode.Created);
        using var writer = site.Client();
        writer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", writerToken["secret"]!.GetValue<string>());
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await writer.GetAsync("/api/v1/spaces")).StatusCode);
        var page = await Json(await writer.PostAsJsonAsync("/api/v1/pages", new { spaceId, parentId = (string?)null, title = "API integration handbook", markdown = "API integration" }), HttpStatusCode.Created);
        var pageId = page["id"]!.GetValue<string>();
        await Json(await writer.PostAsJsonAsync($"/api/v1/pages/{pageId}/publish", new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await writer.GetAsync("/api/v1/search?q=handbok")).StatusCode);
        var tokenId = writerToken["token"]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(owner, HttpMethod.Delete, $"/api/profile/api-tokens/{tokenId}", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await writer.GetAsync($"/api/v1/pages/{pageId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(owner, HttpMethod.Delete, $"/api/profile/api-tokens/{tokenId}", new { })).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Forbidden_answers_scope_missing_problem()
    {
        await using var site = await PersonalApi.RightsSite.CreateAsync();
        await site.SetLevelAsync("write");
        using var reader = await site.TokenClientAsync(["read"]);
        var missing = await reader.PostAsJsonAsync("/api/v1/pages", new { });
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        var body = (await missing.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Equal("scope_missing", body["code"]!.GetValue<string>());
        Assert.Equal("Token nemá rozsah Zápis.", body["title"]!.GetValue<string>());
    }

    /// <summary>
    /// Guessing tokens is bounded per client address: past the failure budget every credential that is not found answers 429 until the
    /// window ends, while a valid token from the same office address keeps working (SEC-A-4). A request without a credential spends the
    /// address's personal-api budget, so an anonymous flood ends in 429 as well, not in an endless series of 401s.
    /// </summary>
    [PostgreSqlFact]
    public async Task Failed_credentials_and_anonymous_floods_are_bounded_per_client_address()
    {
        await using var site = await PersonalApi.RightsSite.CreateAsync();
        await site.SetLevelAsync("read");
        var valid = await site.TokenClientAsync(["read"]);
        Assert.Equal(HttpStatusCode.OK, (await valid.GetAsync("/api/v1/spaces")).StatusCode);

        using var guesser = site.Site.Client();
        for (var guess = 0; guess < PersonalApiCredentialThrottle.FailureLimit; guess++)
        {
            guesser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sat_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
            Assert.Equal(HttpStatusCode.Unauthorized, (await guesser.GetAsync("/api/v1/spaces")).StatusCode);
        }
        var throttled = await guesser.GetAsync("/api/v1/spaces");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);
        Assert.Equal("rate_limited", (await throttled.Content.ReadFromJsonAsync<JsonNode>())!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await valid.GetAsync("/api/v1/spaces")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await guesser.GetAsync("/api/v1/spaces")).StatusCode);

        using var anonymous = site.Site.Client();
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 130 && statuses.LastOrDefault() != HttpStatusCode.TooManyRequests; attempt++)
        {
            statuses.Add((await anonymous.GetAsync("/api/v1/spaces")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.All(statuses.SkipLast(1), status => Assert.Equal(HttpStatusCode.Unauthorized, status));
    }

    /// <summary>O11: the Personal API searches hybrid like the web, still in the published scope only, with the same additive
    /// fields. AI is configured and the index built through the admin endpoints; the fake model gives the query and the
    /// section one concept.</summary>
    [PostgreSqlFact]
    public async Task Personal_search_stays_published_and_serializes_the_passage()
    {
        await using var site = await RightsSite.CreateAsync(isolated: true);
        site.Site.Services.GetRequiredService<FakeAiClientFactory>().Meaning = text =>
            text.Contains("vzdálený přístup", StringComparison.OrdinalIgnoreCase) || text.Contains("Připojení zvenku", StringComparison.Ordinal)
                ? "remote access from home"
                : text;
        await site.SetLevelAsync("read");
        using var api = await site.TokenClientAsync(["read"]);
        var owner = site.Owner;
        var spaceId = await site.FirstSpaceAsync(api);
        var page = await Json(await Send(owner, HttpMethod.Post, "/api/pages", new
        {
            spaceId, parentId = (string?)null, title = "Práce z domova", contentJson = IndexFixture.Rich("Přes klienta a telefon.", ("Připojení zvenku", "zvenku")),
            markdown = "## Připojení zvenku\nPřes klienta a telefon.",
        }), HttpStatusCode.OK);
        var pageId = page["id"]!.GetValue<string>();
        await Json(await Send(owner, HttpMethod.Post, $"/api/pages/{pageId}/publish", new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        var draft = await Json(await Send(owner, HttpMethod.Post, "/api/pages", new
        {
            spaceId, parentId = (string?)null, title = "Vzdálený přístup koncept", contentJson = IndexFixture.Rich("Koncept."), markdown = "Koncept.",
        }), HttpStatusCode.OK);

        await AtlasHttpSite.GrantOwnAiAsync(owner);
        await Json(await Send(owner, HttpMethod.Put, "/api/admin/ai/profiles/embeddings", new
        {
            enabled = true, provider = "openai-compatible", location = "company", baseUrl = "http://lmstudio.example:1234/v1", model = "text-embedding-bge-m3",
            dimensions = 1024, timeoutSeconds = 30, maxConcurrency = 4, nativeJsonSchema = true, clearApiKey = false, expectedVersion = (string?)null,
        }), HttpStatusCode.OK);
        await Json(await Send(owner, HttpMethod.Put, "/api/admin/ai/switch", new { enabled = true, expectedVersion = (string?)null }), HttpStatusCode.OK);
        var generation = await Json(await Send(owner, HttpMethod.Post, "/api/admin/ai/index/generations", new { mode = "build" }), HttpStatusCode.OK);
        var generationId = generation["id"]!.GetValue<string>();
        var runner = site.Site.Services.GetRequiredService<IndexRunner>();
        for (var attempt = 0; ; attempt++)
        {
            // The probe service verifies the new profile at once; a worker round does nothing before that.
            await runner.RunUntilIdleAsync(DateTimeOffset.UtcNow.AddMinutes(1), default);
            var building = (await Json(await owner.GetAsync("/api/admin/ai/index"), HttpStatusCode.OK))["building"]!;
            if (building["indexedPages"]!.GetValue<int>() == 1 && building["pendingTasks"]!.GetValue<int>() == 0)
            {
                break;
            }
            Assert.True(attempt < 200, "The index was not built.");
            await Task.Delay(50);
        }
        Assert.True((await Json(await Send(owner, HttpMethod.Post, $"/api/admin/ai/index/generations/{generationId}/verify"), HttpStatusCode.OK))["canActivate"]!.GetValue<bool>());
        await Json(await Send(owner, HttpMethod.Post, $"/api/admin/ai/index/generations/{generationId}/activate",
            new { expectedVersion = generation["version"]!.GetValue<string>() }), HttpStatusCode.OK);
        for (var attempt = 0; !(await Json(await owner.GetAsync("/api/ai/capabilities"), HttpStatusCode.OK))["semanticSearch"]!.GetValue<bool>(); attempt++)
        {
            Assert.True(attempt < 200, "Semantic search did not become available.");
            await Task.Delay(50);
        }

        var result = await Json(await api.GetAsync("/api/v1/search?q=" + Uri.EscapeDataString("vzdálený přístup")), HttpStatusCode.OK);
        Assert.True(result["semantic"]!.GetValue<bool>());
        var item = Assert.Single(result["items"]!.AsArray())!;
        Assert.Equal((pageId, true, false), (item["id"]!.GetValue<string>(), item["semantic"]!.GetValue<bool>(), item["isDraft"]!.GetValue<bool>()));
        var passage = item["passage"]!;
        Assert.Equal(("Přes klienta a telefon.", "Připojení zvenku", "zvenku"),
            (passage["text"]!.GetValue<string>(), passage["headingTitle"]!.GetValue<string>(), passage["headingId"]!.GetValue<string>()));
        Assert.Equal(3, passage.AsObject().Count);
        Assert.Equal("zvenku", item["headingId"]!.GetValue<string>());
        // The web's unified scope finds the draft lexically as well; the token never sees it.
        var web = await Json(await owner.GetAsync("/api/search?q=" + Uri.EscapeDataString("vzdálený přístup")), HttpStatusCode.OK);
        Assert.Contains(web["items"]!.AsArray(), hit => hit!["id"]!.GetValue<string>() == draft["id"]!.GetValue<string>() && hit["isDraft"]!.GetValue<bool>());
        Assert.Equal(2, web["items"]!.AsArray().Count);
    }

    private static async Task ExpireAsync(AtlasHttpSite site, Guid tokenId)
    {
        // Manipulate only this disposable schema to test expiry without sleeping a day.
        Assert.Equal(1, await site.ExecuteAsync("UPDATE \"PersonalApiToken\" SET \"ExpiresAt\" = \"ActiveFrom\" + interval '1 microsecond' WHERE \"Id\"=@id", ("id", tokenId)));
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? payload = null) => AtlasHttpSite.SendAsync(client, method, path, payload);
    private static Task<JsonNode> Json(HttpResponseMessage response, HttpStatusCode expected) => AtlasHttpSite.Json(response, expected);
}
