using System.Net;
using System.Text.Json;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Atlas.Server.Tests.Objects;

/// <summary>
/// The object path and view sessions over the real pipeline: the wire names and shapes the web client codes against
/// (camelCase JSON, hub method and event names), a page edited in its editor reaching the board session that shows its card, and
/// presence from a page view reaching the board with the colleague's view.
/// </summary>
public sealed class ObjectsLiveHttpTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";

    [PostgreSqlFact]
    public async Task A_board_session_hears_a_page_saved_in_its_editor_and_the_presence_of_whoever_edits_it()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_objects_live_", settings => settings["Modules:Boards:Enabled"] = "true", shared: true);
        var cookies = new CookieContainer();
        using var admin = site.CreateDefaultClient(new Uri("https://localhost"), new CookieContainerHandler(cookies));
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Operations", contentJson = Rich, markdown = "" }), HttpStatusCode.OK);
        var pageId = page["id"]!.GetValue<string>();
        var boardId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/boards", new { title = "Wall" }),
            HttpStatusCode.Created))["id"]!.GetValue<string>();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/boards/{boardId}/operations", new
        {
            operationId = Guid.NewGuid(),
            changes = new object[] { new { type = "create", item = new { id = Guid.NewGuid(), kind = "page", x = 0, y = 0, width = 240, height = 160, z = 0, targetResourceId = pageId, props = new { display = "card" } } } }
        }), HttpStatusCode.OK);

        var read = await AtlasHttpSite.Json(await admin.GetAsync($"/api/objects/{boardId}"), HttpStatusCode.OK);
        Assert.Equal(["id", "typeId", "scope", "spaceId", "ownerId", "version", "createdAt", "createdBy", "updatedAt", "editingModel", "canEdit", "title", "data"],
            read.AsObject().Select(property => property.Key));
        Assert.Equal(("atlas.board", "space", 1L, "boardOps", "Wall"), (read["typeId"]!.GetValue<string>(), read["scope"]!.GetValue<string>(),
            read["version"]!.GetValue<long>(), read["editingModel"]!.GetValue<string>(), read["title"]!.GetValue<string>()));
        var refused = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/objects/{boardId}/changes",
            new { changeId = Guid.NewGuid(), expectedVersion = 1, payload = new { } }), HttpStatusCode.BadRequest);
        Assert.Equal("unsupported_change", refused["code"]!.GetValue<string>());
        var library = await AtlasHttpSite.Json(await admin.GetAsync("/api/objects?type=atlas.board"), HttpStatusCode.OK);
        Assert.Equal("""{"items":[],"total":0,"page":1,"pageSize":50,"canCreate":false}""", library.ToJsonString());

        var heard = new List<(string Method, JsonElement Message)>();
        await using var board = BoardLiveHttpTests.Connect(site, cookies);
        await using var editor = BoardLiveHttpTests.Connect(site, cookies);
        foreach (var method in new[] { "ObjectChanged", "ObjectPresence", "ViewMembers", "ObjectRevoked" })
        {
            board.On<JsonElement>(method, message =>
            {
                lock (heard)
                {
                    heard.Add((method, message.Clone()));
                }
            });
        }
        await board.StartAsync();
        await editor.StartAsync();
        var joined = await board.InvokeAsync<JsonElement>("JoinView", "board:" + boardId);
        Assert.Equal($$"""{"members":[{"objectId":"{{boardId}}","typeId":"atlas.board","version":1},{"objectId":"{{pageId}}","typeId":"atlas.page","version":1}]}""",
            joined.GetRawText());
        await editor.InvokeAsync<JsonElement>("JoinView", "page:" + pageId);
        var notJoined = await Assert.ThrowsAsync<HubException>(() => editor.InvokeAsync("Subscribe", "obj:" + pageId));
        Assert.EndsWith("unsupported", notJoined.Message, StringComparison.Ordinal);

        var saved = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{pageId}",
            new { title = "Operations", contentJson = Rich.Replace("""{"type":"paragraph"}""", """{"type":"paragraph","content":[{"type":"text","text":"Runbook"}]}"""), markdown = "Runbook", expectedVersion = 1 }), HttpStatusCode.OK);
        var changed = await NextAsync(heard, "ObjectChanged", message => message.GetProperty("objectId").GetString() == pageId);
        Assert.Equal($$$"""{"objectId":"{{{pageId}}}","version":{{{saved["version"]!.GetValue<long>()}}},"change":{"kind":"working"}}""", changed.GetRawText());

        await board.InvokeAsync("SetViewport", "board:" + boardId, new[] { Guid.Parse(pageId) });
        await editor.InvokeAsync("SendObjectPresence", Guid.Parse(pageId), new { cursor = new { from = 1, to = 4 } });
        var presence = await NextAsync(heard, "ObjectPresence", _ => true);
        Assert.Equal((pageId, "Administrator", "page:" + pageId, 4), (presence.GetProperty("objectId").GetString(), presence.GetProperty("displayName").GetString(),
            presence.GetProperty("via").GetString(), presence.GetProperty("payload").GetProperty("cursor").GetProperty("to").GetInt32()));
        Assert.Equal(["objectId", "userId", "displayName", "color", "payload", "via"], presence.EnumerateObject().Select(property => property.Name));
    }

    private static async Task<JsonElement> NextAsync(List<(string Method, JsonElement Message)> heard, string method, Func<JsonElement, bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            lock (heard)
            {
                foreach (var (name, message) in heard)
                {
                    if (name == method && match(message))
                    {
                        return message;
                    }
                }
            }
            await Task.Delay(20, timeout.Token);
        }
    }
}
