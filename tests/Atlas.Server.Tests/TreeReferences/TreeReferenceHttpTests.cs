using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.TreeReferences;

/// <summary>What HTTP and MCP add to the reference node commands (their rules are TreeReferenceTests'): the routes and status codes of
/// the web and /api/v1 with the publish scope, the MCP tools with their audit, and G5 for a space switched out of AI.</summary>
public sealed class TreeReferenceHttpTests
{
    [PostgreSqlFact]
    public async Task The_web_the_api_and_mcp_place_and_remove_a_reference()
    {
        await using var f = await McpSite.CreateAsync();
        var space = await f.FirstSpaceAsync();
        var page = (await PageAsync(f.Admin, space, "Příručka", Rich(Paragraph("Obsah.")), "Obsah.")).Id;

        var web = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/spaces/{space}/tree/references",
            new { targetId = page, anchor = "uvod" }), HttpStatusCode.Created);
        Assert.Equal(("reference", "Příručka", "atlas.page", "uvod", true), (web["type"]!.GetValue<string>(), web["title"]!.GetValue<string>(),
            web["targetTypeId"]!.GetValue<string>(), web["anchor"]!.GetValue<string>(), web["readable"]!.GetValue<bool>()));
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Delete, $"/api/tree/references/{web["id"]}", null)).StatusCode);

        await f.AdminTokenAsync("read");
        using var api = f.Site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"], "Publisher")).Secret);
        var created = await api.PostAsJsonAsync($"/api/v1/spaces/{space}/tree/references", new { targetId = page });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await AtlasHttpSite.Json(created, HttpStatusCode.Created))["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/v1/tree/references/{id}")).StatusCode);
        using var reader = f.Site.Client();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(f.Admin, f.Site.Password, ["read"], ["rest"], "Reader")).Secret);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/v1/spaces/{space}/tree/references", new { targetId = page })).StatusCode);

        var (tokenId, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"], "Agent");
        var client = await f.ClientAsync(secret);
        var added = await CallAsync(client, "atlas_add_tree_reference", new { spaceId = space, targetId = page });
        Assert.True(added.IsError != true, Text(added));
        var reference = Structured(added)["referenceId"]!.GetValue<string>();
        var removed = await CallAsync(client, "atlas_remove_tree_reference", new { referenceId = reference });
        Assert.True(removed.IsError != true, Text(removed));
        await f.ExcludeFromAiAsync(space);
        var excluded = await CallAsync(client, "atlas_add_tree_reference", new { spaceId = space, targetId = page });
        Assert.Equal(404, Structured(excluded)["status"]!.GetValue<int>());
        var audit = (string?)await f.Site.ScalarAsync("""
            SELECT string_agg("Operation" || ':' || "Outcome", '|' ORDER BY "Id") FROM "ApiActivity" WHERE "TokenId"=@token
            """, ("token", Guid.Parse(tokenId)));
        Assert.Equal("atlas_add_tree_reference:ok|atlas_remove_tree_reference:ok|atlas_add_tree_reference:not_found", audit);
    }
}
