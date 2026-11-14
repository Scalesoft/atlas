using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageOrganization;

public sealed class PageOrganizationHttpTests
{
    [PostgreSqlFact]
    public async Task Tree_move_trash_untrash_rename_and_trash_list_round_trip_over_http()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_organization_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var first = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "First", contentJson = rich, markdown = "" }), HttpStatusCode.OK);
        var second = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Second", contentJson = rich, markdown = "" }), HttpStatusCode.OK);

        var tree = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/tree"), HttpStatusCode.OK);
        Assert.Equal(2, tree["items"]!.AsArray().Count);
        var version = tree["version"]!.GetValue<long>();

        var moved = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{second["id"]!.GetValue<string>()}/move",
            new { parentId = first["id"]!.GetValue<string>(), beforePageId = (string?)null, expectedTreeVersion = version }), HttpStatusCode.OK);
        version = moved["version"]!.GetValue<long>();

        var renamed = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{first["id"]!.GetValue<string>()}/title",
            new { title = "Renamed", expectedVersion = 1 }), HttpStatusCode.OK);
        Assert.Equal("Renamed", renamed["title"]!.GetValue<string>());

        var trashed = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{first["id"]!.GetValue<string>()}/trash",
            new { includeSubtree = true, expectedTreeVersion = version, expectedVersion = 2 }), HttpStatusCode.OK);
        version = trashed["version"]!.GetValue<long>();
        var trash = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/trash"), HttpStatusCode.OK);
        Assert.Equal(2, trash["items"]!.AsArray().Count);

        var restored = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{first["id"]!.GetValue<string>()}/untrash",
            new { parentId = (string?)null, includeSubtree = true, expectedTreeVersion = version }), HttpStatusCode.OK);
        Assert.Equal(2, restored["affectedIds"]!.AsArray().Count);
    }
}
