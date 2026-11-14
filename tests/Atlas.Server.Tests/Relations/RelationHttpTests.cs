using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Relations;

/// <summary>What HTTP and MCP add to the handlers (spec §8, §13.3): the routes on the web and /api/v1 with a read token, the exact JSON
/// shapes, and G5 for MCP: a space switched out of AI is 404 for its contents and a lock in an object's relations.</summary>
public sealed class RelationHttpTests
{
    [PostgreSqlFact]
    public async Task Web_api_token_and_mcp_read_the_same_contents_and_relations()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.SpaceAsync("Obsah");
        var hidden = await f.SpaceAsync("Mimo AI");
        var (reader, readerId) = await f.UserAsync("reader");
        await f.GrantAsync(space, readerId, "Reader");
        await f.GrantAsync(hidden, readerId, "Reader");
        var (target, _) = await PageAsync(f.Admin, space, "Cíl", Rich(Paragraph("Cíl")), "Cíl");
        var (source, _) = await PageAsync(f.Admin, space, "Zdroj", Rich(Link(target)), "Zdroj");
        var (beyond, _) = await PageAsync(f.Admin, hidden, "Dál", Rich(Paragraph("Dál")), "Dál");
        var (outside, _) = await PageAsync(f.Admin, hidden, "Venku", Rich(Link(source), Link(beyond)), "Venku");
        var reference = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/spaces/{space}/tree/references",
            new { targetId = outside }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        await f.ExcludeFromAiAsync(hidden);

        var contents = await AtlasHttpSite.Json(await reader.GetAsync($"/api/spaces/{space}/contents"), HttpStatusCode.OK);
        Assert.Equal(["items", "summary", "nextCursor"], contents.AsObject().Select(property => property.Key));
        var row = contents["items"]!.AsArray().Single(item => item!["title"]!.GetValue<string>() == "Zdroj")!.AsObject();
        Assert.Equal(["id", "typeId", "title", "path", "scope", "spaceId", "hostId", "usedCount", "linksIn", "linksOut", "comments", "updatedAt", "canEdit"],
            row.Select(property => property.Key));
        Assert.Equal((1, 1, "atlas.page", false), (row["linksIn"]!.GetValue<int>(), row["linksOut"]!.GetValue<int>(), row["typeId"]!.GetValue<string>(),
            row["canEdit"]!.GetValue<bool>()));
        Assert.Equal(["unused", "noIncoming", "broken", "attached"], contents["summary"]!["relations"]!.AsObject().Select(property => property.Key));
        Assert.Equal(2, contents["summary"]!["types"]!["atlas.page"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync($"/api/spaces/{space}/contents?relation=orphans")).StatusCode);
        var relations = await AtlasHttpSite.Json(await reader.GetAsync($"/api/objects/{source}/relations?depth=1&kinds=link"), HttpStatusCode.OK);
        Assert.Equal(["id", "typeId", "title", "spaceId", "spaceName", "readable"], relations["nodes"]![0]!.AsObject().Select(property => property.Key));
        Assert.Equal(["sourceId", "targetId", "kind", "count", "anchor", "draft", "partId", "excerpt", "card"], relations["edges"]![0]!.AsObject().Select(property => property.Key));
        Assert.Equal(2, relations["edges"]!.AsArray().Count);

        // The personal API: the same reads with a read token on the channel rest.
        using var api = f.Site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(reader, f.Site.Password, ["read"], ["rest"])).Secret);
        Assert.Equal(contents["summary"]!.ToJsonString(),
            (await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/spaces/{space}/contents"), HttpStatusCode.OK))["summary"]!.ToJsonString());
        Assert.Equal(relations.ToJsonString(),
            (await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/objects/{source}/relations?kinds=link"), HttpStatusCode.OK)).ToJsonString());

        // MCP as the same reader: the excluded space is missing, its page a lock among the relations.
        var mcp = await f.ClientAsync((await IssueAsync(reader, f.Site.Password, ["read"], ["mcp"])).Secret);
        var listed = Structured(await CallAsync(mcp, "atlas_list_space_contents", new { spaceId = space }));
        Assert.Equal(2, listed["items"]!.AsArray().Count);
        Assert.Equal("not_found", ErrorCode(await CallAsync(mcp, "atlas_list_space_contents", new { spaceId = hidden })));
        var around = Structured(await CallAsync(mcp, "atlas_get_relations", new { objectId = source }));
        Assert.False(around["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == outside)!["readable"]!.GetValue<bool>());
        Assert.Null(around["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == outside)!["title"]);
        Assert.Equal("not_found", ErrorCode(await CallAsync(mcp, "atlas_get_relations", new { objectId = outside })));
        // Depth 2 never continues through the excluded page, and the tree's reference node to it is not listed at all.
        var deep = Structured(await CallAsync(mcp, "atlas_get_relations", new { objectId = source, depth = 2 }));
        Assert.DoesNotContain(deep["nodes"]!.AsArray(), node => node!["id"]!.GetValue<string>() == beyond);
        Assert.DoesNotContain(Structured(await CallAsync(mcp, "atlas_get_space_tree", new { spaceId = space }))["items"]!.AsArray(),
            item => item!["pageId"]!.GetValue<string>() == reference);
        Assert.DoesNotContain(Structured(await CallAsync(mcp, "atlas_list_pages", new { spaceId = space }))["items"]!.AsArray(),
            item => item!["pageId"]!.GetValue<string>() == reference);
        // The web still shows the reader the page of the excluded space: G5 narrows MCP only.
        Assert.True((await AtlasHttpSite.Json(await reader.GetAsync($"/api/objects/{source}/relations"), HttpStatusCode.OK))["nodes"]!.AsArray()
            .Single(node => node!["id"]!.GetValue<string>() == outside)!["readable"]!.GetValue<bool>());
        Assert.Contains((await AtlasHttpSite.Json(await reader.GetAsync($"/api/spaces/{space}/tree"), HttpStatusCode.OK))["items"]!.AsArray(),
            item => item!["id"]!.GetValue<string>() == reference && item["title"]!.GetValue<string>() == "Venku");
    }

    private static string Link(string pageId) =>
        $$$"""{"type":"paragraph","content":[{"type":"pageReference","attrs":{"id":"{{{pageId}}}","title":"Odkaz","url":"#/pages/{{{pageId}}}"}}]}""";
}
