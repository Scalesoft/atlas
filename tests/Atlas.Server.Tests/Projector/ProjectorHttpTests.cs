using System.Net;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Projector;

/// <summary>The module switch (spec §4: <c>Modules:Projector:Enabled</c>, off by default) and the cookie API over HTTP.</summary>
public sealed class ProjectorHttpTests
{
    [PostgreSqlFact]
    public async Task Off_by_default_the_wiki_has_no_projector_route_and_does_not_announce_the_module()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_projector_off_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);

        Assert.DoesNotContain(Routes(site), route => route.StartsWith("/api/projector", StringComparison.Ordinal) || route.StartsWith("/api/v1/projector", StringComparison.Ordinal));
        var modules = await AtlasHttpSite.Json(await admin.GetAsync("/api/modules"), HttpStatusCode.OK);
        Assert.DoesNotContain("projector", modules.ToJsonString(), StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task On_the_web_reads_the_catalog_and_marks_a_catalog_page_through_the_cookie_api_and_a_token_resolves()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_projector_on_", settings => settings["Modules:Projector:Enabled"] = "true", shared: true);
        using var admin = site.Client();
        var adminId = (await site.CompleteSetupAsync(admin))["id"]!.GetValue<string>();
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var page = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Katalog", contentJson = rich, markdown = "" }), HttpStatusCode.OK))["id"]!.GetValue<string>();

        var modules = await AtlasHttpSite.Json(await admin.GetAsync("/api/modules"), HttpStatusCode.OK);
        var marked = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/projector/pages/{page}/catalog", new { isCatalog = true }),
            HttpStatusCode.OK);
        var catalog = await AtlasHttpSite.Json(await admin.GetAsync($"/api/projector/spaces/{spaceId}/catalog"), HttpStatusCode.OK);
        // Review C3-2: the page the web loads says it is a catalog page, so the web needs no catalog to show the chip.
        Assert.True((await AtlasHttpSite.Json(await admin.GetAsync($"/api/pages/{page}"), HttpStatusCode.OK))["isCatalog"]!.GetValue<bool>());
        using var token = await TokenAsync(site, admin, adminId);
        var resolved = await AtlasHttpSite.Json(await token.GetAsync($"/api/v1/projector/spaces/{spaceId}/resolve?keys=portal.ssl"), HttpStatusCode.OK);
        var requirements = await AtlasHttpSite.Json(await admin.GetAsync($"/api/projector/pages/{page}/requirements"), HttpStatusCode.OK);
        var overview = await AtlasHttpSite.Json(await admin.GetAsync($"/api/projector/spaces/{spaceId}/overview?filter=nonsense"), HttpStatusCode.BadRequest);

        Assert.Contains("""{"id":"projector","version":1}""", modules.ToJsonString(), StringComparison.Ordinal);
        Assert.True(marked["isCatalog"]!.GetValue<bool>());
        Assert.Equal("""{"definitions":[],"warnings":[]}""", catalog.ToJsonString());
        Assert.Equal("""{"items":[],"warnings":["Požadavek „portal.ssl“ v katalogu není."]}""", resolved.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        Assert.Equal((0, 0), (requirements["blocks"]!.AsArray().Count, requirements["summary"]!["percent"]!.GetValue<int>()));
        Assert.Equal("invalid_filter", overview["code"]?.GetValue<string>());
        // The web's space-wide reads are throttled per user as its other space-wide reads (search, maintenance) are.
        var throttled = site.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } route && route.StartsWith("/api/projector/", StringComparison.Ordinal) && !route.Contains("/pages/", StringComparison.Ordinal))
            .Select(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()?.PolicyName ?? "none").ToArray();
        Assert.Equal(Enumerable.Repeat("interactive-search", 6), throttled);
        Assert.DoesNotContain("/api/projector/resolve", Routes(site));
    }

    [PostgreSqlFact]
    public async Task A_resolve_over_a_catalog_chain_longer_than_the_cap_answers_400_and_the_server_lives_on()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_projector_chain_", settings => settings["Modules:Projector:Enabled"] = "true", shared: true);
        using var admin = site.Client();
        var adminId = (await site.CompleteSetupAsync(admin))["id"]!.GetValue<string>();
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var page = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Katalog", contentJson = rich, markdown = "" }), HttpStatusCode.OK))["id"]!.GetValue<string>();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{page}/publish", new { expectedVersion = 1 }), HttpStatusCode.OK);
        // 20 000 blocks, each requiring the next: what fits on one catalog page, stored as the editor would have saved it.
        var chain = string.Join(",", Enumerable.Range(0, 20_000).Select(i =>
            $$"""{"type":"requirement","attrs":{"key":"k{{i}}","requires":"{{(i + 1 < 20_000 ? $"k{i + 1}" : "")}}"},"content":[{"type":"paragraph"}]}"""));
        await site.ExecuteAsync("""UPDATE "PageRevision" SET "ContentJson"=@json WHERE "PageId"=@page""",
            ("json", """{"format":"atlas-rich","version":1,"plugins":[{"id":"atlas-requirements","version":1}],"document":{"type":"doc","content":[""" + chain + "]}}"),
            ("page", Guid.Parse(page)));
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/projector/pages/{page}/catalog", new { isCatalog = true }), HttpStatusCode.OK);

        using var token = await TokenAsync(site, admin, adminId);
        var resolved = await token.GetAsync($"/api/v1/projector/spaces/{spaceId}/resolve?keys=k0");

        Assert.Equal("dependency_too_deep", (await AtlasHttpSite.Json(resolved, HttpStatusCode.BadRequest))["code"]?.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/projector/spaces/{spaceId}/catalog")).StatusCode);
    }

    /// <summary>A read token of the administrator on the channel rest, after the administrator allows themself the level read.</summary>
    private static async Task<HttpClient> TokenAsync(AtlasHttpSite site, HttpClient owner, string ownerId)
    {
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(owner, HttpMethod.Put, $"/api/admin/users/{ownerId}/api-access",
            new { level = "read", expectedVersion = (string?)null }), HttpStatusCode.OK);
        var (_, secret) = await Atlas.Server.Tests.Mcp.McpSite.IssueAsync(owner, site.Password, ["read"], ["rest"]);
        var client = site.Client();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private static string[] Routes(AtlasHttpSite site)
    {
        return site.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText ?? "").ToArray();
    }
}
