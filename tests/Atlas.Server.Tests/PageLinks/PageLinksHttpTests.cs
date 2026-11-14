using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.PageLinks.PageLinksFixture;

namespace Atlas.Server.Tests.PageLinks;

public sealed class PageLinksHttpTests
{
    [PostgreSqlFact]
    public async Task Connections_endpoint_serializes_links_here()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_page_links_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        var space = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/settings"), HttpStatusCode.OK);
        var target = await PublishAsync(admin, spaceId, "Target", Doc(Text("target")));
        var targetId = target["id"]!.GetValue<string>();
        var source = await PublishAsync(admin, spaceId, "Source", Doc(Ref(Guid.Parse(targetId))));
        // The link is derived by the outbox's delivery, which this site has no queue for.
        await AtlasTestHost.RefreshPageDerivedAsync(site.Services.GetRequiredService<AtlasDatabase>(), Guid.Parse(spaceId));

        var connections = await AtlasHttpSite.Json(await admin.GetAsync($"/api/pages/{targetId}/connections?limit=5"), HttpStatusCode.OK);

        // Without AI the JSON is the phase-1 shape: no related field at all (the web hides the section).
        Assert.Equal(["linksHere"], connections.AsObject().Select(x => x.Key));
        var item = Assert.Single(connections["linksHere"]!["items"]!.AsArray())!;
        Assert.Equal(1, connections["linksHere"]!["total"]!.GetValue<int>());
        Assert.Equal(source["id"]!.GetValue<string>(), item["id"]!.GetValue<string>());
        Assert.Equal(spaceId, item["spaceId"]!.GetValue<string>());
        Assert.Equal(space["name"]!.GetValue<string>(), item["spaceName"]!.GetValue<string>());
        Assert.Equal("Source", item["title"]!.GetValue<string>());
        Assert.Equal(1, (await AtlasHttpSite.Json(await admin.GetAsync($"/api/pages/{targetId}/connections"), HttpStatusCode.OK))["linksHere"]!["total"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/pages/{targetId}/connections?limit=51")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Publication_check_and_references_endpoints_bind_requests()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_page_links_check_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        var missing = Guid.NewGuid();
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Draft", contentJson = Doc(Ref(missing)), markdown = "Draft" }), HttpStatusCode.OK);
        var pageId = page["id"]!.GetValue<string>();
        var version = page["version"]!.GetValue<int>();

        var check = await AtlasHttpSite.Json(await admin.GetAsync($"/api/pages/{pageId}/publication-check?expectedVersion={version}"), HttpStatusCode.OK);
        Assert.Equal(version, check["version"]!.GetValue<int>());
        var finding = Assert.Single(check["links"]!.AsArray())!;
        Assert.Equal(missing.ToString(), finding["targetPageId"]!.GetValue<string>());
        Assert.Equal("unavailable", finding["state"]!.GetValue<string>());
        Assert.Null(finding["title"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/pages/{pageId}/publication-check")).StatusCode);

        var references = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages/references",
            new { ids = new[] { pageId, missing.ToString() } }), HttpStatusCode.OK);
        var item = Assert.Single(references["items"]!.AsArray())!;
        Assert.Equal(pageId, item["id"]!.GetValue<string>());
        Assert.Equal("Draft", item["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages/references",
            new { ids = new[] { "not-a-page" } })).StatusCode);
    }

    internal static async Task<System.Text.Json.Nodes.JsonNode> PublishAsync(HttpClient client, string spaceId, string title, string rich)
    {
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title, contentJson = rich, markdown = title }), HttpStatusCode.OK);
        return await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, $"/api/pages/{page["id"]!.GetValue<string>()}/publish",
            new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
    }
}
