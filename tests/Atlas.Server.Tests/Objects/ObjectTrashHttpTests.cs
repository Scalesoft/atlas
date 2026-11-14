using System.Net;
using System.Net.Http.Headers;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Objects;

/// <summary>What HTTP and MCP add to the object trash (its rules are ObjectTrashTests'): the web lists, restores and purges; /api/v1 and MCP
/// list and restore with their audit, never purge (unified trash spec §2, rule 6); MCP leaves out a space switched out of AI (G5).</summary>
public sealed class ObjectTrashHttpTests
{
    [PostgreSqlFact]
    public async Task The_web_purges_while_the_api_and_mcp_list_and_restore()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        async Task<string> TrashedAsync(string name, string scope, string? spaceId = null)
        {
            var id = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/objects",
                new { typeId = "atlas.contact", scope, spaceId, data = new { name } }), HttpStatusCode.Created))["id"]!.GetValue<string>();
            Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Delete, $"/api/objects/{id}", null)).StatusCode);
            return id;
        }
        var jana = await TrashedAsync("Jana", "wiki");
        var karel = await TrashedAsync("Karel", "wiki");
        var team = await TrashedAsync("Tým", "space", space);

        // The trash of the whole shared site: the test's three are there (a wiki administrator sees every test's).
        var ours = new[] { jana, karel, team };
        var web = await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/objects/trash?type=atlas.contact"), HttpStatusCode.OK);
        Assert.Equal(3, web["items"]!.AsArray().Count(item => ours.Contains(item!["id"]!.GetValue<string>())));
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/objects/{karel}/purge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/objects/{karel}/purge", null)).StatusCode);

        await f.AdminTokenAsync("read");
        using var api = f.Site.Client();
        var (publisherId, publisher) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"], "Publisher");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", publisher);
        var listed = await AtlasHttpSite.Json(await api.GetAsync("/api/v1/objects/trash?scope=wiki"), HttpStatusCode.OK);
        Assert.Equal([jana], listed["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).Where(ours.Contains));
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync($"/api/v1/objects/{jana}/purge", null)).StatusCode);
        using var reader = f.Site.Client();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(f.Admin, f.Site.Password, ["read"], ["rest"], "Reader")).Secret);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/v1/objects/{jana}/restore", null)).StatusCode);
        var restored = await AtlasHttpSite.Json(await api.PostAsync($"/api/v1/objects/{jana}/restore", null), HttpStatusCode.OK);
        Assert.Equal(2, restored["version"]!.GetValue<int>());
        Assert.Equal("Jana", (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/objects/{jana}"), HttpStatusCode.OK))["title"]!.GetValue<string>());

        var (tokenId, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"], "Agent");
        var client = await f.ClientAsync(secret);
        Assert.Equal(new[] { team }, Items(await CallAsync(client, "atlas_list_object_trash")).Select(item => item!["objectId"]!.GetValue<string>()).Where(ours.Contains));
        await f.ExcludeFromAiAsync(space);
        Assert.DoesNotContain(Items(await CallAsync(client, "atlas_list_object_trash")), item => ours.Contains(item!["objectId"]!.GetValue<string>()));
        Assert.Equal("not_found", ErrorCode(await CallAsync(client, "atlas_restore_object", new { objectId = team })));
        Assert.Equal("not_found", ErrorCode(await CallAsync(client, "atlas_restore_object", new { objectId = jana })));
        Assert.DoesNotContain(await client.ListToolsAsync(), tool => tool.Name.Contains("purge", StringComparison.Ordinal));
        var audit = (string?)await f.Site.ScalarAsync("""
            SELECT string_agg("Operation" || ':' || "Outcome", '|' ORDER BY "Id") FROM "ApiActivity" WHERE "TokenId"=@token
            """, ("token", Guid.Parse(tokenId)));
        Assert.Equal("atlas_restore_object:not_found|atlas_restore_object:not_found", audit);
        Assert.Equal(1L, await f.Site.ScalarAsync("""SELECT count(*) FROM "ApiActivity" WHERE "Operation"='object.restore' AND "Outcome"='ok' AND "TokenId"=@token""",
            ("token", Guid.Parse(publisherId))));
    }
}
