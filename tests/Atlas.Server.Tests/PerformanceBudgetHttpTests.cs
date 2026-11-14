using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;
using Xunit.Abstractions;

namespace Atlas.Server.Tests;

/// <summary>
/// SQL round-trip budgets of the requests behind opening a page (5.0),
/// read from the Server-Timing header as an ordinary, non-admin reader sees them. The budgets pin today's
/// statement counts (+1 for a physical connection opened on a cold pool) so a regression fails here.
/// Durable logout adds one indexed session-revocation lookup during cookie authentication on every request.
/// The second check after the account lock shares SetActor's existing statement; no cache delays logout enforcement.
/// Reaching the audit's three per read would need the lock-and-authorize prologue in one SQL function,
/// which would split authorization between C# and SQL; the account and the page are each read once instead.
/// </summary>
public sealed class PerformanceBudgetHttpTests(ITestOutputHelper output)
{
    private static readonly (string Name, Func<Guid, Guid, string> Path, int Budget)[] Reads =
    [
        ("spaces", (_, _) => "/api/spaces", 9),
        // The pages, and one query per other content type in the tree (the site runs with Boards on: its boards; reference nodes).
        ("tree", (space, _) => $"/api/spaces/{space}/tree", 13),
        ("page", (_, page) => $"/api/pages/{page}", 13),
        // The read that opens a page records the visit in it (3.6-G N7): the page's statements and the visit's own, not a second request.
        ("page-visit", (_, page) => $"/api/pages/{page}?visit=true", 32),
        // The page and its bundle in one transaction: each part's own read under the page's locks (personal state, tags, links here).
        ("bundle", (_, page) => $"/api/pages/{page}?bundle=true", 18),
        ("tags", (_, page) => $"/api/pages/{page}/tags", 12),
        ("personal-state", (_, page) => $"/api/pages/{page}/personal-state", 13),
        // The relation sources go as one batched command (RelationAnchor.ReadAllAsync), then the nodes; whatever the space holds.
        ("contents", (space, _) => $"/api/spaces/{space}/contents", 10),
        ("relations", (_, page) => $"/api/objects/{page}/relations", 6),
        // „Použito na“ (plan 3.1 M5): the relation sources and the nodes, then one statement per kind of place for the reasons.
        ("usage", (_, page) => $"/api/usage/{page}", 6),
        ("usage-count", (_, page) => $"/api/usage/{page}/count", 6),
        ("space-usage", (space, _) => $"/api/spaces/{space}/usage", 10),
        // The actor lock and one statement for the readable occurrences, the count and the page (PERF1-1), however many there are.
        ("entities", (_, _) => "/api/entities/contact", 5),
        // The omnisearch's one request per query (UX SE3-1): the search, its spaces and each content type's own search.
        ("search", (_, _) => "/api/search?q=budget&limit=50", 14),
        // The reads of 3.0 (PERF1-4): today's count +1 where the read has headroom. A note in the trash is in the object trash.
        ("entities-export", (_, _) => "/api/entities/contact/export?format=csv", 6),
        ("notifications", (_, _) => "/api/notifications", 7),
        ("notifications-unread", (_, _) => "/api/notifications/unread", 6),
        ("personal-items", (_, _) => "/api/personal/items", 6),
        ("personal-shared", (_, _) => "/api/personal/shared", 6),
        ("object-trash", (_, _) => "/api/objects/trash", 10),
        // The home dashboard (4.0): Co se změnilo in one statement after the account lock; Naposledy navštívené (pages and boards) and the drafts of Čeká na mě.
        ("home-changes", (_, _) => "/api/me/changes?limit=8", 5),
        ("home-recent", (_, _) => "/api/me/library/recent?limit=6", 9),
        ("home-drafts", (_, _) => "/api/me/library/drafts?limit=3", 6),
    ];

    /// <summary>Reads only an administrator may make, measured as the wiki administrator: the trash of every space and of one.</summary>
    private static readonly (string Name, Func<Guid, string> Path, int Budget)[] AdminReads =
    [
        ("space-trash", _ => "/api/space-trash", 7),
        ("trash-of-space", space => $"/api/spaces/{space}/trash", 12),
    ];

    [PostgreSqlFact]
    public async Task Opening_a_page_stays_within_its_sql_budget_and_ships_compressed_content()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_budget_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        var paragraph = string.Join(" ", Enumerable.Repeat("Příliš žluťoučký kůň úpěl ďábelské ódy \"v uvozovkách\".", 40));
        var rich = new JsonObject
        {
            ["format"] = "atlas-rich", ["version"] = 1,
            ["document"] = new JsonObject
            {
                ["type"] = "doc",
                ["content"] = new JsonArray(Enumerable.Range(0, 20).Select(_ => (JsonNode)new JsonObject
                {
                    ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = paragraph })
                }).ToArray())
            }
        }.ToJsonString();
        var created = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (Guid?)null, title = "Budget", contentJson = rich, markdown = paragraph }), HttpStatusCode.OK);
        var pageId = created["id"]!.GetValue<Guid>();
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{pageId}/publish",
            new { expectedVersion = created["version"]!.GetValue<int>() }), HttpStatusCode.OK);

        var readerLogin = TestLogin.For("reader");
        var invitation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = readerLogin, displayName = "Reader", isWikiAdmin = false }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId = invitation["user"]!["id"]!.GetValue<Guid>(), role = "Reader", expectedVersion = (string?)null })).StatusCode);
        using var reader = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, "/api/auth/login",
            new { username = readerLogin, password = site.Password }), HttpStatusCode.OK);

        var over = new List<string>();
        foreach (var (name, path, budget) in Reads)
        {
            using var response = await reader.GetAsync(path(spaceId, pageId));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var statements = Server.Platform.Hosting.AtlasServerTiming.Statements(string.Join(",", response.Headers.GetValues("Server-Timing")));
            Assert.NotNull(statements);
            output.WriteLine($"{name}: {statements} SQL statements (budget {budget})");
            if (statements > budget) over.Add($"{name} {statements} > {budget}");
        }
        // A board as its reader opens it (one note: the item kinds each read their own targets, 11 to 18 statements by them).
        var board = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/spaces/{spaceId}/boards", new { title = "Budget" }), HttpStatusCode.Created);
        Measure("board", reader, $"/api/boards/{board["id"]!.GetValue<Guid>()}", 12);
        foreach (var (name, path, budget) in AdminReads)
        {
            Measure(name, admin, path(spaceId), budget);
        }
        void Measure(string name, HttpClient client, string path, int budget)
        {
            using var response = client.GetAsync(path).GetAwaiter().GetResult();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var statements = AtlasServerTiming(response);
            output.WriteLine($"{name}: {statements} SQL statements (budget {budget})");
            if (statements > budget) over.Add($"{name} {statements} > {budget}");
        }
        var visit = await AtlasHttpSite.SendAsync(reader, HttpMethod.Post, $"/api/pages/{pageId}/visit");
        Assert.Equal(HttpStatusCode.OK, visit.StatusCode);
        output.WriteLine($"visit: {AtlasServerTiming(visit)} SQL statements (budget 18)");
        if (AtlasServerTiming(visit) > 18) over.Add($"visit {AtlasServerTiming(visit)} > 18");
        Assert.True(over.Count == 0, "SQL budget exceeded: " + string.Join("; ", over));

        // The web client's page omits the Markdown projection and travels compressed; auth answers do not.
        using var compressed = new HttpRequestMessage(HttpMethod.Get, $"/api/pages/{pageId}");
        compressed.Headers.AcceptEncoding.ParseAdd("br");
        using var page = await reader.SendAsync(compressed);
        Assert.Equal("br", Assert.Single(page.Content.Headers.ContentEncoding));
        using var plain = await reader.GetAsync($"/api/pages/{pageId}");
        var body = await plain.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"markdown\"", body);
        using var csrf = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
        csrf.Headers.AcceptEncoding.ParseAdd("br");
        using var token = await reader.SendAsync(csrf);
        Assert.Empty(token.Content.Headers.ContentEncoding);
    }

    private static int? AtlasServerTiming(HttpResponseMessage response) =>
        Server.Platform.Hosting.AtlasServerTiming.Statements(string.Join(",", response.Headers.GetValues("Server-Timing")));
}
