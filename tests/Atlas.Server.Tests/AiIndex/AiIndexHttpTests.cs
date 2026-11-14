using System.Net;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.AiIndex;

/// <summary>What HTTP adds to the index endpoints: routes, binding of mode, id and expected version, the JSON the web client
/// (<c>aiAdmin.ts</c>) reads, and status codes. Rules (completeness, CAS, WikiAdmin 403) are in the handler tests.</summary>
public sealed class AiIndexHttpTests
{
    [PostgreSqlFact]
    public async Task Index_endpoints_bind_and_answer_only_wiki_admins()
    {
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_ai_index_http_"); // own database: saves the installation-wide embeddings profile row and the AI switch, and builds the one index generation
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        const string index = "/api/admin/ai/index";

        var empty = await AtlasHttpSite.Json(await admin.GetAsync(index), HttpStatusCode.OK);
        Assert.Equal("""{"active":null,"building":null,"retired":[],"queue":{"pending":0,"failing":0,"oldestPendingAt":null}}""", empty.ToJsonString());
        // Building needs a saved, enabled embeddings profile.
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, index + "/generations", new { mode = "build" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, index + "/generations", new { mode = "other" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/ai/profiles/embeddings", new
        {
            enabled = true, provider = "openai-compatible", location = "company", baseUrl = "http://lmstudio.example:1234/v1", model = "text-embedding-bge-m3",
            dimensions = 1024, timeoutSeconds = 30, maxConcurrency = 4, nativeJsonSchema = true, clearApiKey = false, expectedVersion = (string?)null,
        })).StatusCode);

        var started = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, index + "/generations", new { mode = "build" }), HttpStatusCode.OK);
        var id = started["id"]!.GetValue<string>();
        Assert.Equal(("Building", "text-embedding-bge-m3", "headings-1800@3"),
            (started["state"]!.GetValue<string>(), started["model"]!.GetValue<string>(), started["chunkingVersion"]!.GetValue<string>()));
        foreach (var field in new[] { "modelVersion", "eligiblePages", "indexedPages", "chunks", "pendingTasks", "failingTasks", "missing", "createdAt", "activatedAt", "version" })
        {
            Assert.True(started.AsObject().ContainsKey(field), field);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, index + "/generations", new { mode = "rebuild" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, "/api/admin/ai/switch",
            new { enabled = true, expectedVersion = (string?)null })).StatusCode);
        // An empty wiki is complete at once; verification spends nothing.
        var verified = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"{index}/generations/{id}/verify"), HttpStatusCode.OK);
        Assert.Equal("""{"complete":true,"missingPages":0,"stalePages":0,"dimensionsOk":true,"sanity":{"checked":0,"hitsAt5":0},"canActivate":true}""",
            verified.ToJsonString());
        Assert.Equal(HttpStatusCode.Conflict, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"{index}/generations/{id}/activate",
            new { expectedVersion = "2020-01-01T00:00:00.0000000Z" })).StatusCode);
        var activated = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"{index}/generations/{id}/activate",
            new { expectedVersion = started["version"]!.GetValue<string>() }), HttpStatusCode.OK);
        Assert.Equal(id, activated["active"]!["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"{index}/generations/{id}/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"{index}/generations/{Guid.NewGuid()}/verify")).StatusCode);

        // The overview of A3 carries the index tile.
        var overview = await AtlasHttpSite.Json(await admin.GetAsync("/api/admin/ai"), HttpStatusCode.OK);
        Assert.Equal(("Active", "text-embedding-bge-m3"), (overview["index"]!["state"]!.GetValue<string>(), overview["index"]!["model"]!.GetValue<string>()));
    }
}
