using System.Net;
using System.Text.Json;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Atlas.Server.Tests.Objects;

/// <summary>
/// The sources of a board's cards change while the board is open (COL2-8): a rename reaches its viewers as ObjectChanged, a trash as
/// ObjectRevoked, a restore as ViewMembers to who may read it again, a purge as nothing more; who cannot read a source hears none of it.
/// </summary>
public sealed class BoardSourcesLiveHttpTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
    private static readonly Uri s_base = new("https://localhost");

    [PostgreSqlFact]
    public async Task Board_viewers_hear_their_sources_renamed_trashed_restored_and_purged()
    {
        // Its own site, for now: see BoardLiveHttpTests (beside it on the shared site one of the two heard nothing of a sent broadcast).
        await using var site = await AtlasHttpSite.CreateAsync("atlas_board_sources_", settings => settings["Modules:Boards:Enabled"] = "true");
        var adminCookies = new CookieContainer();
        var readerCookies = new CookieContainer();
        using var admin = site.CreateDefaultClient(s_base, new CookieContainerHandler(adminCookies));
        using var reader = site.CreateDefaultClient(s_base, new CookieContainerHandler(readerCookies));
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        await BoardLiveHttpTests.AddReaderAsync(site, admin, reader, spaceId);

        async Task<string> ContactAsync(string name, string scope) => (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/objects",
            new { typeId = "atlas.contact", scope, data = new { name } }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        var jana = await ContactAsync("Jana", "wiki");
        var mine = await ContactAsync("Moje", "private");
        var pageId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Operations", contentJson = Rich, markdown = "" }), HttpStatusCode.OK))["id"]!.GetValue<string>();
        var boardId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/boards", new { title = "Wall" }),
            HttpStatusCode.Created))["id"]!.GetValue<string>();
        object Card(string contactId, int z) => new { type = "create", item = new { id = Guid.NewGuid(), kind = "object", x = z * 300, y = 0, width = 280, height = 160, z,
            props = new { objectId = contactId, typeId = "atlas.contact", view = "card" } } };
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/boards/{boardId}/operations", new
        {
            operationId = Guid.NewGuid(),
            changes = new[]
            {
                Card(jana, 0), Card(mine, 1),
                new { type = "create", item = new { id = Guid.NewGuid(), kind = "page", x = 0, y = 300, width = 240, height = 160, z = 2, targetResourceId = pageId, props = new { display = "card" } } }
            }
        }), HttpStatusCode.OK);

        var heard = new Heard();
        await using var adminHub = BoardLiveHttpTests.Connect(site, adminCookies);
        await using var readerHub = BoardLiveHttpTests.Connect(site, readerCookies);
        foreach (var method in new[] { "ObjectChanged", "ViewMembers", "ObjectRevoked" })
        {
            adminHub.On<JsonElement>(method, message => heard.Add("admin", method, message));
            readerHub.On<JsonElement>(method, message => heard.Add("reader", method, message));
        }
        await adminHub.StartAsync();
        await readerHub.StartAsync();
        var view = "board:" + boardId;
        Assert.Equal(4, (await adminHub.InvokeAsync<JsonElement>("JoinView", view)).GetProperty("members").GetArrayLength());
        var readerMembers = Ids((await readerHub.InvokeAsync<JsonElement>("JoinView", view)).GetProperty("members"));
        Assert.Contains(jana, readerMembers);
        Assert.DoesNotContain(mine, readerMembers);

        // A contact renamed: its fields to everyone who reads it.
        var mark = heard.Mark();
        var renamed = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/objects/{jana}/changes",
            new { changeId = Guid.NewGuid(), expectedVersion = 1, payload = new { fields = new { name = "Jana Nová" } } }), HttpStatusCode.OK);
        foreach (var who in new[] { "admin", "reader" })
        {
            var change = await heard.NextAsync(mark, who, "ObjectChanged", jana);
            Assert.Equal(("Jana Nová", renamed["version"]!.GetValue<long>()), (change.GetProperty("change").GetProperty("fields").GetProperty("name").GetString(), change.GetProperty("version").GetInt64()));
        }

        // Trashed: revoked for everyone who held it; restored: a member again, with the restore's version.
        mark = heard.Mark();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Delete, $"/api/objects/{jana}", null)).StatusCode);
        await heard.NextAsync(mark, "admin", "ObjectRevoked", jana);
        await heard.NextAsync(mark, "reader", "ObjectRevoked", jana);
        mark = heard.Mark();
        var restored = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/objects/{jana}/restore", null), HttpStatusCode.OK);
        foreach (var who in new[] { "admin", "reader" })
        {
            var added = (await heard.NextAsync(mark, who, "ViewMembers", jana, "added")).GetProperty("added");
            Assert.Equal(restored["version"]!.GetValue<long>(), added.EnumerateArray().Single(member => member.GetProperty("objectId").GetString() == jana).GetProperty("version").GetInt64());
        }
        // And it is followed again: the next rename reaches the reader.
        mark = heard.Mark();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/objects/{jana}/changes",
            new { changeId = Guid.NewGuid(), expectedVersion = restored["version"]!.GetValue<long>(), payload = new { fields = new { name = "Jana Stará" } } }), HttpStatusCode.OK);
        await heard.NextAsync(mark, "reader", "ObjectChanged", jana);

        // A private contact restored: back for its owner, never told to who cannot read it.
        mark = heard.Mark();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Delete, $"/api/objects/{mine}", null)).StatusCode);
        await heard.NextAsync(mark, "admin", "ObjectRevoked", mine);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/objects/{mine}/restore", null), HttpStatusCode.OK);
        await heard.NextAsync(mark, "admin", "ViewMembers", mine, "added");

        // A page renamed (working, to its editors), trashed (published, then revoked) and restored (a member again).
        mark = heard.Mark();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{pageId}/title", new { title = "Provoz", expectedVersion = 1 }), HttpStatusCode.OK);
        Assert.Equal("working", (await heard.NextAsync(mark, "admin", "ObjectChanged", pageId)).GetProperty("change").GetProperty("kind").GetString());
        var tree = (await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/tree"), HttpStatusCode.OK))["version"]!.GetValue<long>();
        mark = heard.Mark();
        tree = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{pageId}/trash",
            new { includeSubtree = true, expectedTreeVersion = tree, expectedVersion = 2 }), HttpStatusCode.OK))["version"]!.GetValue<long>();
        await heard.NextAsync(mark, "admin", "ObjectRevoked", pageId, within: TimeSpan.FromSeconds(3));
        mark = heard.Mark();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{pageId}/untrash",
            new { parentId = (string?)null, includeSubtree = true, expectedTreeVersion = tree }), HttpStatusCode.OK);
        await heard.NextAsync(mark, "admin", "ViewMembers", pageId, "added");

        // Trashed and purged: revoked once, then nothing of it, and a new join no longer lists it.
        mark = heard.Mark();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Delete, $"/api/objects/{jana}", null)).StatusCode);
        await heard.NextAsync(mark, "reader", "ObjectRevoked", jana);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/objects/{jana}/purge", null)).StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(1));
        await readerHub.InvokeAsync("LeaveView", view);
        Assert.DoesNotContain(jana, Ids((await readerHub.InvokeAsync<JsonElement>("JoinView", view)).GetProperty("members")));

        Assert.Empty(heard.About("reader", mine));
        Assert.DoesNotContain(heard.About("reader", jana, from: mark), item => item.Method != "ObjectRevoked");
    }

    private static List<string> Ids(JsonElement members) => members.EnumerateArray().Select(member => member.GetProperty("objectId").GetString()!).ToList();

    private sealed class Heard
    {
        private readonly List<(string Who, string Method, JsonElement Message)> m_items = [];

        public void Add(string who, string method, JsonElement message)
        {
            lock (m_items)
            {
                m_items.Add((who, method, message.Clone()));
            }
        }

        public int Mark()
        {
            lock (m_items)
            {
                return m_items.Count;
            }
        }

        /// <summary>The messages to <paramref name="who"/> that name the object, as an id or inside a member list.</summary>
        public List<(string Method, JsonElement Message)> About(string who, string objectId, int from = 0)
        {
            lock (m_items)
            {
                return m_items.Skip(from).Where(item => item.Who == who && Names(item.Message, objectId, null)).Select(item => (item.Method, item.Message)).ToList();
            }
        }

        public async Task<JsonElement> NextAsync(int from, string who, string method, string objectId, string? list = null, TimeSpan? within = null)
        {
            using var timeout = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(10));
            while (true)
            {
                lock (m_items)
                {
                    foreach (var item in m_items.Skip(from))
                    {
                        if (item.Who == who && item.Method == method && Names(item.Message, objectId, list))
                        {
                            return item.Message;
                        }
                    }
                }
                await Task.Delay(20, timeout.Token);
            }
        }

        private static bool Names(JsonElement message, string objectId, string? list)
        {
            if (list is null && message.TryGetProperty("objectId", out var id) && id.GetString() == objectId)
            {
                return true;
            }
            return message.EnumerateObject().Any(property => (list is null || property.Name == list) && property.Value.ValueKind == JsonValueKind.Array
                && property.Value.EnumerateArray().Any(member => member.ValueKind == JsonValueKind.Object && member.TryGetProperty("objectId", out var memberId) && memberId.GetString() == objectId
                    || member.ValueKind == JsonValueKind.String && member.GetString() == objectId));
        }
    }
}
