using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.PersonalItems;

/// <summary>What HTTP and MCP add to the personal notes (their rules are PersonalItemsAccessTests'): the web's routes and the generic share
/// route of kind item, and AI parity over /api/v1 and MCP — create, list, read and edit as the token owner, with the publish scope for writes.</summary>
public sealed class PersonalItemsHttpTests
{
    private static Task<McpSite> SiteAsync() => McpSite.CreateAsync(settings: settings => settings["Modules:Boards:Enabled"] = "true");

    [PostgreSqlFact]
    public async Task The_web_creates_shares_and_lists_personal_items()
    {
        await using var f = await SiteAsync();
        var (colleague, colleagueId) = await f.UserAsync("kolega");
        var note = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/objects",
            new { typeId = "atlas.note", scope = "private", data = new { title = "Poznámka", text = "Text" } }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        var mine = Assert.Single((await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/personal/items"), HttpStatusCode.OK)).AsArray())!;
        Assert.Equal((note, "Text"), (mine["id"]!.GetValue<string>(), mine["excerpt"]!.GetValue<string>()));
        // Personal boards were dropped (owner, 29. 10. 2026).
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/personal/boards", new { title = "Moje tabule" })).StatusCode);

        // The owner, even a WikiAdmin, finds the colleague through the candidates; the share is the generic grant route.
        // By their logins, unique on the shared site.
        Assert.Equal(colleagueId, (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/access/candidates?kind=user&q={f.Login(colleagueId)}"), HttpStatusCode.OK))[0]!["id"]!.GetValue<string>());
        var me = (string)(await f.Site.ScalarAsync("""SELECT "Username" FROM "UserAccount" WHERE "Id"=@id::uuid""", ("id", f.AdminId)))!;
        Assert.Empty((await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/access/candidates?kind=user&q={me}&exceptMe=true"), HttpStatusCode.OK)).AsArray());
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/access/resources/item/{note}/grants",
            new { subjectKind = "user", subjectId = colleagueId, level = "read" })).StatusCode);
        var shared = (await AtlasHttpSite.Json(await colleague.GetAsync("/api/personal/shared"), HttpStatusCode.OK)).AsArray();
        Assert.Equal((note, "read", "Poznámka"), (shared.Single()!["id"]!.GetValue<string>(), shared.Single()!["level"]!.GetValue<string>(),
            shared.Single()!["title"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.OK, (await colleague.GetAsync($"/api/objects/{note}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(colleague, HttpMethod.Put, $"/api/access/resources/item/{note}/grants",
            new { subjectKind = "user", subjectId = f.AdminId, level = "read" })).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Mcp_and_the_personal_api_keep_personal_items_as_the_web_does()
    {
        await using var f = await SiteAsync();
        await f.AdminTokenAsync("read");
        var agent = await f.ClientAsync((await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"])).Secret);

        var note = Ok(await CallAsync(agent, "atlas_create_note", new { title = "Plán", text = "- první" }))["noteId"]!.GetValue<string>();
        Assert.Equal(2, Ok(await CallAsync(agent, "atlas_edit_note", new { noteId = note, text = "- druhý", expectedVersion = 1 }))["version"]!.GetValue<long>());
        var read = Ok(await CallAsync(agent, "atlas_get_note", new { noteId = note }));
        Assert.Equal(("Plán", "- druhý", true), (read["title"]!.GetValue<string>(), read["markdown"]!.GetValue<string>(), read["canEdit"]!.GetValue<bool>()));
        // A board needs its space again: personal boards were dropped (owner, 29. 10. 2026).
        Assert.True((await CallAsync(agent, "atlas_create_board", new { title = "Osobní" })).IsError);
        var listed = Assert.Single(Items(await CallAsync(agent, "atlas_list_personal")))!;
        Assert.Equal((note, "druhý"), (listed["id"]!.GetValue<string>(), listed["excerpt"]!.GetValue<string>()));
        Assert.Empty(Items(await CallAsync(agent, "atlas_list_shared_with_me")));

        using var api = f.Site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(f.Admin, f.Site.Password, ["read"], ["rest"], "Reader")).Secret);
        Assert.Single((await AtlasHttpSite.Json(await api.GetAsync("/api/v1/personal/items"), HttpStatusCode.OK)).AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsJsonAsync("/api/v1/objects",
            new { typeId = "atlas.note", scope = "private", data = new { title = "Bez zápisu" } })).StatusCode);
        Assert.Equal("Plán", (await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/objects/{note}"), HttpStatusCode.OK))["title"]!.GetValue<string>());
        Ok(await CallAsync(agent, "atlas_trash_note", new { noteId = note }));
        Assert.Equal("not_found", ErrorCode(await CallAsync(agent, "atlas_get_note", new { noteId = note })));
    }

    private static JsonNode Ok(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.True(result.IsError != true, Text(result));
        return Structured(result);
    }
}
