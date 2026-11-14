using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.ExternalSharing;

public sealed class ExternalSharingHttpTests
{
    [PostgreSqlFact]
    public async Task Public_payload_has_no_cached_YouTrack_state_private_URL_or_unsanitized_markdown()
    {
        await using var fixture = await SharingSite.CreateAsync();
        await fixture.SetPolicy(true, null);
        const string rich = """{"format":"atlas-rich","version":1,"plugins":[{"id":"attachments","version":1},{"id":"youtrack","version":1},{"id":"heading-anchors","version":1}],"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Published issue "},{"type":"youtrackIssue","attrs":{"id":"private-internal-id","key":"APP-42","title":"Release checklist","state":"private-cached-state","url":"https://private-youtrack.example/issue/APP-42"}}]}]}}""";
        const string markdown = "Published issue [APP-42: Release checklist · private-cached-state](https://private-youtrack.example/issue/APP-42)";
        var page = await AtlasHttpSite.Json(await fixture.Admin.GetAsync(fixture.PagePath), HttpStatusCode.OK);
        page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Put, fixture.PagePath,
            new { title = "Issue reference", contentJson = rich, markdown, expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Post, fixture.PagePath + "/publish",
            new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        var issue = await AtlasHttpSite.Json(await fixture.Issue(), HttpStatusCode.Created);
        var shared = await AtlasHttpSite.Json(await fixture.Anonymous.GetAsync("/api/shared/" + issue["url"]!.GetValue<string>()[3..]), HttpStatusCode.OK);
        var payload = shared.ToJsonString();
        Assert.DoesNotContain("private-cached-state", payload);
        Assert.DoesNotContain("private-youtrack", payload);
        Assert.DoesNotContain("private-internal-id", payload);
        Assert.Null(shared["markdown"]);
        Assert.Contains("APP-42: Release checklist", shared["contentJson"]!.GetValue<string>());
        var publicDocument = JsonNode.Parse(shared["contentJson"]!.GetValue<string>())!;
        Assert.Equal(new[] { "attachments", "heading-anchors" }, publicDocument["plugins"]!.AsArray().Select(plugin => plugin!["id"]!.GetValue<string>()));
        Assert.All(publicDocument["plugins"]!.AsArray(), plugin => Assert.Equal(1, plugin!["version"]!.GetValue<int>()));
        var original = await AtlasHttpSite.Json(await fixture.Admin.GetAsync(fixture.PagePath), HttpStatusCode.OK);
        Assert.Contains("\"id\":\"youtrack\"", original["contentJson"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task Public_OpenAPI_page_of_512_KiB_is_accepted_and_shared_as_reference()
    {
        await using var fixture = await SharingSite.CreateAsync();
        await fixture.SetPolicy(true, null);
        var rich = OpenApiDocuments.Rich();
        var page = await AtlasHttpSite.Json(await fixture.Admin.GetAsync(fixture.PagePath), HttpStatusCode.OK);
        page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Put, fixture.PagePath,
            new { title = "API", contentJson = rich, markdown = OpenApiDocuments.Markdown, expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        Assert.Equal(rich, page["contentJson"]!.GetValue<string>());
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Post, fixture.PagePath + "/publish",
            new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        var issue = await AtlasHttpSite.Json(await fixture.Issue(), HttpStatusCode.Created);
        var shared = await AtlasHttpSite.Json(await fixture.Anonymous.GetAsync("/api/shared/" + issue["url"]!.GetValue<string>()[3..]), HttpStatusCode.OK);
        var content = shared["contentJson"]!.GetValue<string>();
        Assert.DoesNotContain("uploadedBy", content);
        Assert.Null(shared["markdown"]);
        var data = OpenApiDocuments.Block(content)["attrs"]!["data"]!;
        Assert.Equal("reference", data["mode"]!.GetValue<string>());
        Assert.Equal(OpenApiDocuments.LargestSource, data["source"]!.GetValue<string>());
        Assert.Equal(OpenApiDocuments.Filename, data["filename"]!.GetValue<string>());
        var original = await AtlasHttpSite.Json(await fixture.Admin.GetAsync(fixture.PagePath), HttpStatusCode.OK);
        Assert.Equal(rich, original["contentJson"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task Policy_defaults_disabled_and_pins_a_generation()
    {
        await using var fixture = await SharingSite.CreateAsync();
        var policy = await fixture.Policy();
        Assert.False(policy["enabled"]!.GetValue<bool>());
        Assert.Null(policy["version"]);
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.Issue()).StatusCode);
        policy = await fixture.SetPolicy(true, null);
        Assert.Equal(HttpStatusCode.Conflict, (await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Put, fixture.PolicyPath,
            new { enabled = false, expectedVersion = (string?)null })).StatusCode);
        var issued = await AtlasHttpSite.Json(await fixture.Issue(), HttpStatusCode.Created);
        var url = issued["url"]!.GetValue<string>();
        Assert.StartsWith("/s/sas_", url);
        var publicPath = "/api/shared/" + url[3..];
        Assert.Equal(HttpStatusCode.OK, (await fixture.Anonymous.GetAsync(publicPath)).StatusCode);
        policy = await fixture.SetPolicy(false, policy["version"]!.GetValue<string>());
        await fixture.SetPolicy(true, policy["version"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Anonymous.GetAsync(publicPath)).StatusCode);
        var listed = await AtlasHttpSite.Json(await fixture.Admin.GetAsync(fixture.LinksPath), HttpStatusCode.OK);
        Assert.DoesNotContain("sas_", listed.ToJsonString());
        Assert.DoesNotContain("tokenHash", listed.ToJsonString());
        Assert.Equal("revoked", listed["items"]![0]!["status"]!.GetValue<string>());
    }

    [PostgreSqlFact]
    public async Task Link_keeps_published_revision_private_payload_and_permanent_revocation()
    {
        await using var fixture = await SharingSite.CreateAsync();
        await fixture.SetPolicy(true, null);
        var issued = await AtlasHttpSite.Json(await fixture.Issue(), HttpStatusCode.Created);
        var token = issued["url"]!.GetValue<string>()[3..];
        var publicPath = "/api/shared/" + token;
        var page = await AtlasHttpSite.Json(await fixture.Admin.GetAsync(fixture.PagePath), HttpStatusCode.OK);
        page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Put, fixture.PagePath,
            new { title = "Later private draft", contentJson = SharingSite.Rich("private text"), markdown = "private text", expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Post, fixture.PagePath + "/publish",
            new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        var response = await fixture.Anonymous.GetAsync(publicPath);
        var shared = await AtlasHttpSite.Json(response, HttpStatusCode.OK);
        Assert.Equal("First publication", shared["title"]!.GetValue<string>());
        Assert.DoesNotContain("private text", shared.ToJsonString());
        Assert.Null(shared["spaceId"]);
        Assert.Null(shared["createdById"]);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
        var linkId = issued["link"]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Delete, fixture.LinksPath + "/" + linkId)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Delete, fixture.LinksPath + "/" + linkId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Anonymous.GetAsync(publicPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Anonymous.GetAsync("/api/shared/invalid")).StatusCode);
        Assert.DoesNotContain(fixture.Site.Logs.Entries, entry => entry.Message.Contains(token, StringComparison.Ordinal));
    }

    [PostgreSqlFact]
    public async Task Issue_rejects_invalid_name_expiry_and_private_draft()
    {
        await using var fixture = await SharingSite.CreateAsync();
        await fixture.SetPolicy(true, null);
        foreach (var expiry in new[] { DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(31) })
            Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Issue("Client", expiry)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Issue(" ")).StatusCode);
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Post, "/api/pages",
            new { spaceId = fixture.SpaceId, title = "Private", contentJson = SharingSite.Rich("secret"), markdown = "secret" }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, (await AtlasHttpSite.SendAsync(fixture.Admin, HttpMethod.Post,
            $"/api/pages/{page["id"]!.GetValue<string>()}/share-links", new { name = "No draft", expiresAt = DateTime.UtcNow.AddDays(1) })).StatusCode);
    }
}

internal sealed class SharingSite : IAsyncDisposable
{
    private SharingSite(AtlasHttpSite site, HttpClient admin, HttpClient anonymous, Guid spaceId, Guid pageId)
    { Site = site; Admin = admin; Anonymous = anonymous; SpaceId = spaceId; PageId = pageId; }
    public AtlasHttpSite Site { get; }
    public HttpClient Admin { get; }
    public HttpClient Anonymous { get; }
    public Guid SpaceId { get; }
    public Guid PageId { get; }
    public string PagePath => $"/api/pages/{PageId}";
    public string PolicyPath => $"/api/spaces/{SpaceId}/external-sharing";
    public string LinksPath => PagePath + "/share-links";
    public static async Task<SharingSite> CreateAsync()
    {
        var site = await AtlasHttpSite.CreateAsync("atlas_sharing_http_");
        var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, title = "First publication", contentJson = Rich("public text"), markdown = "public text" }), HttpStatusCode.OK);
        var pageId = Guid.Parse(page["id"]!.GetValue<string>());
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{pageId}/publish",
            new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        return new SharingSite(site, admin, site.Client(), spaceId, pageId);
    }
    public async Task<JsonNode> Policy() => await AtlasHttpSite.Json(await Admin.GetAsync(PolicyPath), HttpStatusCode.OK);
    public async Task<JsonNode> SetPolicy(bool enabled, string? version) => await AtlasHttpSite.Json(
        await AtlasHttpSite.SendAsync(Admin, HttpMethod.Put, PolicyPath, new { enabled, expectedVersion = version }), HttpStatusCode.OK);
    public Task<HttpResponseMessage> Issue(string name = "Client review", DateTime? expiry = null) => AtlasHttpSite.SendAsync(Admin, HttpMethod.Post, LinksPath,
        new { name, expiresAt = expiry ?? DateTime.UtcNow.AddDays(1) });
    public static string Rich(string text) => new JsonObject { ["format"] = "atlas-rich", ["version"] = 1,
        ["document"] = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(new JsonObject { ["type"] = "paragraph",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) }) } }.ToJsonString();
    public async ValueTask DisposeAsync() { Admin.Dispose(); Anonymous.Dispose(); await Site.DisposeAsync(); }
}
