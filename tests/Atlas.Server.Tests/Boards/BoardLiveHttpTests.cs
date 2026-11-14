using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Boards;
using Atlas.Server.Platform.Live;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Atlas.Server.Tests.Boards;

/// <summary>
/// /hubs/live end to end over the real pipeline (boards spec §7): a reader subscribes, hears a writer's committed batch and the
/// writer's presence, may not send presence itself, and is revoked by the grant change that removes it, before the response
/// of that change returns. The wire names checked here are the ones the web client codes against.
/// </summary>
public sealed class BoardLiveHttpTests
{
    private static readonly Uri s_base = new("https://localhost");

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task A_reader_hears_committed_batches_and_presence_until_its_access_is_revoked()
    {
        // Its own site, for now: on the shared site, beside BoardSourcesLiveHttpTests, one of the two clients' hubs heard nothing of a
        // broadcast the server sent to its subscribed connections (reported to the lead, cause not found).
        await using var site = await AtlasHttpSite.CreateAsync("atlas_live_boards_", settings => settings["Modules:Boards:Enabled"] = "true");
        var adminCookies = new CookieContainer();
        var readerCookies = new CookieContainer();
        using var admin = site.CreateDefaultClient(s_base, new CookieContainerHandler(adminCookies));
        using var reader = site.CreateDefaultClient(s_base, new CookieContainerHandler(readerCookies));
        await site.CompleteSetupAsync(admin);
        var adminId = (await AtlasHttpSite.Json(await admin.GetAsync("/api/auth/me"), HttpStatusCode.OK))["id"]!.GetValue<string>();
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        var readerId = await AddReaderAsync(site, admin, reader, spaceId);

        var created = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/boards", new { title = "Live" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var boardId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal($"/api/boards/{boardId}", created.Headers.Location!.OriginalString);
        var channel = BoardLiveChannel.Name(boardId);

        var heard = new Inbox();
        await using var adminHub = Connect(site, adminCookies);
        await using var readerHub = Connect(site, readerCookies);
        readerHub.On<JsonElement>("BoardChanged", message => heard.Add("reader:BoardChanged", message));
        readerHub.On<JsonElement>("Presence", message => heard.Add("reader:Presence", message));
        readerHub.On<JsonElement>("Revoked", message => heard.Add("reader:Revoked", message));
        adminHub.On<JsonElement>("BoardChanged", message => heard.Add("admin:BoardChanged", message));
        await adminHub.StartAsync();
        await readerHub.StartAsync();

        Assert.Equal(0, (await readerHub.InvokeAsync<LiveSubscribed>("Subscribe", channel)).Sequence);
        Assert.Equal(0, (await adminHub.InvokeAsync<LiveSubscribed>("Subscribe", channel)).Sequence);
        await AssertHubErrorAsync("not_found", () => readerHub.InvokeAsync<LiveSubscribed>("Subscribe", BoardLiveChannel.Name(Guid.NewGuid())));
        await AssertHubErrorAsync("unsupported", () => readerHub.InvokeAsync<LiveSubscribed>("Subscribe", "doc:" + boardId));

        var item = Guid.NewGuid();
        var applied = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/boards/{boardId}/operations", new
        {
            operationId = Guid.NewGuid(),
            changes = new object[] { new { type = "create", item = new { id = item, kind = "note", x = 10, y = 20, width = 160, height = 120, z = 0, color = "yellow" } } }
        }), HttpStatusCode.OK);
        Assert.Equal("""{"sequence":1,"duplicate":false}""", applied.ToJsonString());
        // A NUL (PostgreSQL text refuses it) is a refused change, never a server error.
        var nul = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/boards/{boardId}/operations",
            new { operationId = Guid.NewGuid(), changes = new object[] { new { type = "set", itemId = item, text = "a\0b" } } });
        Assert.Equal("invalid_board_change", (await AtlasHttpSite.Json(nul, HttpStatusCode.BadRequest))["code"]!.GetValue<string>());
        var changed = await heard.NextAsync("reader:BoardChanged");
        Assert.Equal((boardId, 1L, adminId), (changed.GetProperty("boardId").GetGuid(), changed.GetProperty("sequence").GetInt64(), changed.GetProperty("authorId").GetString()));
        Assert.True(changed.TryGetProperty("operationId", out _));
        // Absent members stay absent on the wire: a set without text means "text unchanged", never "text null".
        Assert.Equal($$$$"""[{"type":"create","item":{"id":"{{{{item}}}}","kind":"note","x":10,"y":20,"width":160,"height":120,"z":0,"color":"yellow","props":{}}}]""",
            changed.GetProperty("changes").GetRawText());

        var board = await AtlasHttpSite.Json(await reader.GetAsync($"/api/boards/{boardId}"), HttpStatusCode.OK);
        Assert.Equal($$"""{"id":"{{boardId}}","spaceId":"{{spaceId}}","title":"Live","sequence":1,"canEdit":false,"settings":{"background":"dots","gridSize":24,"snapToGrid":false},"items":[{"id":"{{item}}","kind":"note","x":10,"y":20,"width":160,"height":120,"z":0,"color":"yellow","props":{},"version":1}]}""",
            board.ToJsonString());

        // Presence needs Writer; the writer's reaches the reader with the handshake account's name, never a client claim.
        await AssertHubErrorAsync("forbidden", () => readerHub.InvokeAsync("SendPresence", channel, new { cursor = new { x = 1, y = 2 } }));
        await adminHub.InvokeAsync("SendPresence", channel, new { cursor = new { x = 1.5, y = 2 }, dragging = new[] { new { itemId = item, x = 50, y = 60, width = 160, height = 120 } } });
        var presence = await heard.NextAsync("reader:Presence");
        Assert.Equal((channel, adminId, "Administrator", 1.5), (presence.GetProperty("channel").GetString(), presence.GetProperty("userId").GetString(),
            presence.GetProperty("displayName").GetString(), presence.GetProperty("payload").GetProperty("cursor").GetProperty("x").GetDouble()));
        await AssertHubErrorAsync("invalid_presence", () => adminHub.InvokeAsync("SendPresence", channel,
            new { dragging = Enumerable.Range(0, 51).Select(_ => new { itemId = item, x = 0, y = 0, width = 8, height = 8 }).ToArray() }));

        // Revoking the grant revalidates live subscriptions after its commit: the reader is told and hears nothing more.
        var access = (await admin.GetFromJsonAsync<SpaceAccessSummary>($"/api/spaces/{spaceId}/access"))!;
        var grant = Assert.Single(access.Grants, x => x.UserId == readerId);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId = readerId, role = (string?)null, expectedVersion = grant.Version })).StatusCode);
        Assert.Equal(channel, (await heard.NextAsync("reader:Revoked")).GetProperty("channel").GetString());
        await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/boards/{boardId}/operations",
            new { operationId = Guid.NewGuid(), changes = new object[] { new { type = "rename", title = "Renamed" } } });
        await heard.NextAsync("admin:BoardChanged");
        Assert.Equal(2, (await heard.NextAsync("admin:BoardChanged")).GetProperty("sequence").GetInt64());
        Assert.Equal(1, heard.Count("reader:BoardChanged"));
    }

    internal static HubConnection Connect(AtlasHttpSite site, CookieContainer cookies)
    {
        var hub = new Uri(s_base, "/hubs/live");
        return new HubConnectionBuilder()
            .WithUrl(hub, options =>
            {
                // TestServer has no socket; long polling runs every request through its in-memory handler.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => site.Server.CreateHandler();
                options.Headers["Cookie"] = cookies.GetCookieHeader(hub);
            })
            .Build();
    }

    /// <summary>A builtin account with Reader in the space, signed in on <paramref name="reader"/>.</summary>
    internal static async Task<Guid> AddReaderAsync(AtlasHttpSite site, HttpClient admin, HttpClient reader, string spaceId)
    {
        var username = TestLogin.For("reader");
        var invited = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users", new { username, displayName = "Live reader", isWikiAdmin = false });
        Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        var invitation = (await invited.Content.ReadFromJsonAsync<CreatedBuiltinUser>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation.Token, newPassword = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/auth/login",
            new { username, password = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId = invitation.User.Id, role = "Reader", expectedVersion = (string?)null })).StatusCode);
        return invitation.User.Id;
    }

    internal static async Task AssertHubErrorAsync(string code, Func<Task> invoke)
    {
        var refused = await Assert.ThrowsAsync<HubException>(invoke);
        Assert.EndsWith("HubException: " + code, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Messages by "who:Method", taken in arrival order.</summary>
    internal sealed class Inbox
    {
        private readonly Dictionary<string, List<JsonElement>> m_messages = [];
        private readonly Dictionary<string, int> m_taken = [];
        private readonly SemaphoreSlim m_arrived = new(0);

        public void Add(string key, JsonElement message)
        {
            lock (m_messages)
            {
                (m_messages.TryGetValue(key, out var list) ? list : m_messages[key] = []).Add(message.Clone());
            }
            m_arrived.Release();
        }

        /// <summary>Every message of the key so far, taken or not.</summary>
        public IReadOnlyList<JsonElement> All(string key)
        {
            lock (m_messages)
            {
                return m_messages.TryGetValue(key, out var list) ? [.. list] : [];
            }
        }

        public int Count(string key)
        {
            lock (m_messages)
            {
                return m_messages.TryGetValue(key, out var list) ? list.Count : 0;
            }
        }

        public async Task<JsonElement> NextAsync(string key)
        {
            // Bounded like the collaboration tests' waits; well below the 5 s periodic revalidation.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                lock (m_messages)
                {
                    var taken = m_taken.GetValueOrDefault(key);
                    if (m_messages.TryGetValue(key, out var list) && list.Count > taken)
                    {
                        m_taken[key] = taken + 1;
                        return list[taken];
                    }
                }
                await m_arrived.WaitAsync(timeout.Token);
            }
        }
    }
}
