using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Boards;

/// <summary>What /api/v1 and MCP add to the board commands (their rules are BoardHandlerTests' and BoardEditingTests'): the canvas's own
/// batch over a token with the write scope, the space role and G5 as the web decides them, and the audit of every write.</summary>
public sealed class BoardAiParityHttpTests
{
    private const string Contact = """{"type":"contactCard","attrs":{"data":{"version":1,"contacts":[{"name":"Jana Nováková"}]}}}""";

    private static Task<McpSite> SiteAsync() => McpSite.CreateAsync(settings: settings => settings["Modules:Boards:Enabled"] = "true");

    /// <summary>L4: the web's batch endpoint is throttled per account (board-batch), the personal API's by its token budget (personal-api).</summary>
    [PostgreSqlFact]
    public async Task The_batch_endpoints_are_rate_limited()
    {
        await using var f = await SiteAsync();
        var policies = f.Site.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.EndsWith("/boards/{boardId:guid}/operations", StringComparison.Ordinal) == true
                && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true)
            .ToDictionary(e => e.RoutePattern.RawText!, e => e.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal("board-batch", policies["/api/boards/{boardId:guid}/operations"]);
        Assert.Equal("personal-api", policies["/api/v1/boards/{boardId:guid}/operations"]);
    }

    [PostgreSqlFact]
    public async Task Mcp_creates_moves_colours_groups_and_deletes_items_as_the_canvas_does()
    {
        await using var f = await SiteAsync();
        var space = await f.FirstSpaceAsync();
        await f.AdminTokenAsync("read");
        var (tokenId, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        var agent = await f.ClientAsync(secret);

        var board = Ok(await CallAsync(agent, "atlas_create_board", new { spaceId = space, title = "Plán" }))["boardId"]!.GetValue<string>();
        var (note, group) = (Guid.NewGuid(), Guid.NewGuid());
        var created = Ok(await CallAsync(agent, "atlas_edit_board", new
        {
            boardId = board,
            changes = new object[]
            {
                new { type = "create", item = new { id = note, kind = "note", x = 0, y = 0, width = 200, height = 120, z = 1, text = "Nápad", props = new { } } },
                new { type = "create", item = new { kind = "block", x = 300, y = 0, width = 240, height = 160, z = 2, color = "blue-4", props = new { block = JsonNode.Parse(Contact) } } },
                new { type = "create", item = new { id = group, kind = "group", x = -20, y = -20, width = 600, height = 220, z = 0, text = "Skupina", props = new { } } },
                new { type = "parent", itemId = note, parentId = group },
                // P15: a list the board derives from the relations is added by its kind, as from the canvas's component menu.
                new { type = "create", item = new { kind = "links", x = 700, y = 0, width = 340, height = 320, z = 3, props = new { show = "people" } } }
            }
        }));
        var block = created["createdIds"]![1]!.GetValue<string>();
        var links = created["createdIds"]![3]!.GetValue<string>();
        Ok(await CallAsync(agent, "atlas_edit_board", new
        {
            boardId = board,
            changes = new object[]
            {
                new { type = "move", itemId = note, x = 100, y = 50, width = 240, height = 160 },
                new { type = "set", itemId = note, color = "green", text = "Lepší nápad" },
                new { type = "set", itemId = block, color = "teal-2" }
            }
        }));

        var items = Ok(await CallAsync(agent, "atlas_get_board", new { boardId = board }))["items"]!.AsArray().ToDictionary(x => x!["id"]!.GetValue<string>());
        var moved = items[note.ToString()]!;
        Assert.Equal((100d, 50d, 240d, 160d, "green", "Lepší nápad", group.ToString()), (moved["x"]!.GetValue<double>(), moved["y"]!.GetValue<double>(),
            moved["width"]!.GetValue<double>(), moved["height"]!.GetValue<double>(), moved["color"]!.GetValue<string>(), moved["text"]!.GetValue<string>(),
            moved["parent"]!.GetValue<string>()));
        Assert.Equal(("links", "people"), (items[links]!["kind"]!.GetValue<string>(), items[links]!["props"]!["show"]!.GetValue<string>()));
        Assert.Equal(("block", "teal-2", "contactCard"), (items[block]!["kind"]!.GetValue<string>(), items[block]!["color"]!.GetValue<string>(),
            items[block]!["props"]!["block"]!["type"]!.GetValue<string>()));

        // Ungroup, then delete: the canvas's parent without parentId and delete.
        Ok(await CallAsync(agent, "atlas_edit_board", new
        {
            boardId = board, changes = new object[] { new { type = "parent", itemId = note }, new { type = "delete", itemId = group }, new { type = "delete", itemId = note } }
        }));
        Assert.Equal(new[] { block, links }, Ok(await CallAsync(agent, "atlas_get_board", new { boardId = board }))["items"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()));
        Assert.Equal("invalid_board_change", ErrorCode(await CallAsync(agent, "atlas_edit_board", new
        {
            boardId = board, changes = new object[] { new { type = "set", itemId = block, color = "blue-9" } }
        })));
        var audit = (string?)await f.Site.ScalarAsync("""
            SELECT string_agg("Operation" || ':' || "Outcome", '|' ORDER BY "Id") FROM "ApiActivity" WHERE "TokenId"=@token
            """, ("token", Guid.Parse(tokenId)));
        Assert.Equal("atlas_create_board:ok|atlas_edit_board:ok|atlas_edit_board:ok|atlas_edit_board:ok|atlas_edit_board:invalid_board_change", audit);
    }

    [PostgreSqlFact]
    public async Task A_reader_may_read_but_not_edit_a_board_and_a_space_out_of_ai_is_missing()
    {
        await using var f = await SiteAsync();
        var space = await f.FirstSpaceAsync();
        var board = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/spaces/{space}/boards", new { title = "Plán" }),
            HttpStatusCode.Created))["id"]!.GetValue<string>();
        var (readerClient, readerId) = await f.UserAsync("ctenar", "write");
        await f.GrantAsync(space, readerId, "Reader");
        var reader = await f.ClientAsync((await IssueAsync(readerClient, f.Site.Password, ["read", "write"], ["mcp"])).Secret);
        var note = new { type = "create", item = new { kind = "note", x = 0, y = 0, width = 200, height = 120, z = 1, props = new { } } };

        Assert.False(Ok(await CallAsync(reader, "atlas_get_board", new { boardId = board }))["canEdit"]!.GetValue<bool>());
        Assert.Equal("forbidden", ErrorCode(await CallAsync(reader, "atlas_edit_board", new { boardId = board, changes = new object[] { note } })));
        Assert.Equal("forbidden", ErrorCode(await CallAsync(reader, "atlas_delete_board", new { boardId = board })));
        Assert.Equal("forbidden", ErrorCode(await CallAsync(reader, "atlas_create_board", new { spaceId = space, title = "Další" })));

        // M1: a board under a page with its own access the reader does not read is missing over MCP, as on the web.
        var page = await PageAsync(f.Admin, space, "Platy", Rich(Paragraph("Mzdy.")), "Mzdy.");
        var restricted = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/spaces/{space}/boards", new { title = "Mzdy", parentPageId = page.Id }),
            HttpStatusCode.Created))["id"]!.GetValue<string>();
        Assert.Equal(restricted, Ok(await CallAsync(reader, "atlas_get_board", new { boardId = restricted }))["boardId"]!.GetValue<string>());
        await f.Site.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=@id""", ("id", Guid.Parse(page.Id)));
        Assert.Equal("not_found", ErrorCode(await CallAsync(reader, "atlas_get_board", new { boardId = restricted })));

        // G5: a space out of AI and MCP answers as an unreadable one, even to its administrator; the web still works.
        await f.AdminTokenAsync("read");
        var admin = await f.ClientAsync((await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"])).Secret);
        await f.ExcludeFromAiAsync(space);
        Assert.Equal("not_found", ErrorCode(await CallAsync(admin, "atlas_get_board", new { boardId = board })));
        Assert.Equal("not_found", ErrorCode(await CallAsync(admin, "atlas_edit_board", new { boardId = board, changes = new object[] { note } })));
        Assert.Equal("not_found", ErrorCode(await CallAsync(admin, "atlas_create_board", new { spaceId = space, title = "Další" })));
        Assert.Equal(0, (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/boards/{board}"), HttpStatusCode.OK))["sequence"]!.GetValue<long>());
    }

    [PostgreSqlFact]
    public async Task The_personal_api_edits_a_board_with_the_write_scope()
    {
        await using var f = await SiteAsync();
        var space = await f.FirstSpaceAsync();
        await f.AdminTokenAsync("read");
        using var api = f.Site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"], "Writer")).Secret);
        using var draft = f.Site.Client();
        draft.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await IssueAsync(f.Admin, f.Site.Password, ["read"], ["rest"], "Reader")).Secret);

        var board = (await AtlasHttpSite.Json(await api.PostAsJsonAsync($"/api/v1/spaces/{space}/boards", new { title = "Plán" }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        var item = Guid.NewGuid();
        var batch = new
        {
            operationId = Guid.NewGuid(),
            changes = new object[] { new { type = "create", item = new { id = item, kind = "shape", x = 0, y = 0, width = 120, height = 80, z = 1, color = "red", props = new { shape = "ellipse" } } } }
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await draft.PostAsJsonAsync($"/api/v1/boards/{board}/operations", batch)).StatusCode);
        Assert.Equal(1, (await AtlasHttpSite.Json(await api.PostAsJsonAsync($"/api/v1/boards/{board}/operations", batch), HttpStatusCode.OK))["sequence"]!.GetValue<long>());
        var read = await AtlasHttpSite.Json(await draft.GetAsync($"/api/v1/boards/{board}"), HttpStatusCode.OK);
        Assert.Equal((item.ToString(), "red"), (read["items"]![0]!["id"]!.GetValue<string>(), read["items"]![0]!["color"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/v1/boards/{board}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/v1/boards/{board}")).StatusCode);
    }

    private static JsonNode Ok(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.True(result.IsError != true, Text(result));
        return Structured(result);
    }
}
