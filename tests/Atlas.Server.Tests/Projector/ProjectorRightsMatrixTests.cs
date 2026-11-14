using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using Xunit.Abstractions;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>
/// The rights matrix of Projector (owner's request for R4): every route of the web's cookie API, every /api/v1 route and every MCP tool
/// for each kind of actor, against one deployment page and its space. A write goes with a stale version on purpose, so an actor the
/// rights let through ends at 409 version_conflict and nothing changes; "ok" is a 2xx or that 409. Then nothing of what an actor may
/// not see leaks into the space reads: unpublished, trashed and template pages, a page with its own access, and catalog pages that are
/// trashed, unpublished, closed to the actor or in a space the actor cannot read.
/// </summary>
public sealed class ProjectorRightsMatrixTests(ITestOutputHelper output)
{
    private const string Stale = "999";
    private const string B1 = "00000000-0000-4000-8000-0000000000b1", C1 = "00000000-0000-4000-8000-0000000000c1";

    private enum Kind { PageRead, PageWrite, SpaceRead, Deploy }

    /// <summary>Expected outcome per actor and kind of operation: ok, 401, 403 or 404.</summary>
    private static readonly Dictionary<string, Dictionary<Kind, string>> Expected = new()
    {
        ["wiki admin"] = Row("ok", "ok", "ok", "ok"),
        ["space admin"] = Row("ok", "ok", "ok", "ok"),
        ["writer"] = Row("ok", "ok", "ok", "ok"),
        // A root page needs Writer on the space; its reader knows the space and gets 403, as POST /api/pages does (D12, review S3).
        ["reader"] = Row("ok", "403", "ok", "403"),
        // A page grant lists the space as the page tree does, with only what the guest may read.
        ["guest of the page"] = Row("ok", "403", "ok", "404"),
        ["outsider"] = Row("404", "404", "404", "404"),
        ["anonymous"] = Row("401", "401", "401", "401"),
        ["disabled account"] = Row("401", "401", "401", "401"),
        ["read-only token"] = Row("ok", "403", "ok", "403"),
        ["owner lost access"] = Row("404", "404", "404", "404"),
    };

    private static Dictionary<Kind, string> Row(string pageRead, string pageWrite, string spaceRead, string deploy)
        => new() { [Kind.PageRead] = pageRead, [Kind.PageWrite] = pageWrite, [Kind.SpaceRead] = spaceRead, [Kind.Deploy] = deploy };

    private sealed record Actor(string Name, HttpClient? Cookie, HttpClient? Rest, McpClient? Mcp, bool Anonymous = false);

    [PostgreSqlFact]
    public async Task Every_route_and_tool_answers_each_actor_as_its_rights_say_and_nothing_hidden_leaks()
    {
        await using var f = await ProjectorMcpToolTests.SiteAsync();
        var space = await f.SpaceAsync("Nasazení");
        var hiddenSpace = await f.SpaceAsync("Uzavřený prostor");
        var page = await McpSite.PageAsync(f.Admin, space, "KBN", Json(Block(B1, "portal.ssl", "SSL", state: "waiting_for_input", party: "IT KBN")), "");
        var catalog = await McpSite.PageAsync(f.Admin, space, "Katalog", Json(Block(C1, "portal.orders", "Objednávky", requires: "portal.ssl")), "");
        await MarkCatalogAsync(f, catalog.Id);
        await SeedHiddenAsync(f, space, hiddenSpace);
        await f.SetLevelAsync(f.AdminId, "write");

        string? lostId = null, disabledId = null;
        var actors = new List<Actor> { await ActorAsync(f, "wiki admin", f.Admin, null) };
        foreach (var (name, role) in new[] { ("space admin", "Admin"), ("writer", "Writer"), ("reader", "Reader"), ("guest of the page", null), ("outsider", null),
                     ("disabled account", "Writer"), ("owner lost access", "Writer") })
        {
            var (client, id) = await f.UserAsync(name.Replace(' ', '-'), "write");
            if (role == "Admin")
            {
                Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/spaces/{space}/access",
                    new { userId = id, role, expectedVersion = (string?)null }, await AtlasHttpSite.ReauthAsync(f.Admin, f.Site.Password))).StatusCode);
            }
            else if (role is not null)
            {
                await f.GrantAsync(space, id, role);
            }
            if (name == "guest of the page")
            {
                Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/pages/{page.Id}/access/grants",
                    new { userId = id, role = "Reader" })).StatusCode);
            }
            actors.Add(await ActorAsync(f, name, client, id));
            lostId = name == "owner lost access" ? id : lostId;
            disabledId = name == "disabled account" ? id : disabledId;
            if (name == "writer")
            {
                var (_, readRest) = await McpSite.IssueAsync(client, f.Site.Password, ["read"], ["rest"]);
                var (_, readMcp) = await McpSite.IssueAsync(client, f.Site.Password, ["read"], ["mcp"]);
                actors.Add(new Actor("read-only token", null, ProjectorApiTests.Bearer(f, readRest), await f.ClientAsync(readMcp)));
            }
        }
        actors.Add(new Actor("anonymous", f.Site.Client(), f.Site.Client(), null, Anonymous: true));
        // After the tokens and sessions exist: the account is switched off, and the other loses its role.
        await f.Site.ExecuteAsync("""UPDATE "UserAccount" SET "IsActive"=false WHERE "Id"=@id""", ("id", Guid.Parse(disabledId!)));
        var lost = (await AtlasHttpSite.Json(await f.Admin.GetAsync($"/api/spaces/{space}/access"), HttpStatusCode.OK))["grants"]!.AsArray()
            .Single(grant => grant!["userId"]?.GetValue<string>() == lostId)!;
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/spaces/{space}/access",
            new { userId = lostId, role = (string?)null, expectedVersion = lost["version"]!.GetValue<string>() })).StatusCode);

        var operations = Operations(space, page.Id, catalog.Id);
        // The actors' reads and their refused or stale writes change nothing, so the actors go at once (the matrix is 280 calls; one
        // after another it took most of the test and, under a loaded run, the 2-minute inactivity limit). Deployments really create
        // a page, so they follow one after another.
        async Task<List<(string Channel, string Name, Kind Kind, string Actual)>> RunAsync(Actor actor, bool deploy)
        {
            var rows = new List<(string, string, Kind, string)>();
            var mine = operations.Where(operation => (operation.Kind == Kind.Deploy) == deploy);
            if (actor.Name == "disabled account")
            {
                // Its bearer requests are failed credentials, and 30 a minute from one address close it to every token
                // (PersonalApiCredentialThrottle); the 401 comes before any route, so one operation per channel and kind shows it.
                mine = mine.GroupBy(operation => (operation.Channel, operation.Kind)).Select(group => group.First());
            }
            foreach (var (channel, name, kind, call) in mine)
            {
                if (await call(actor) is { } actual)
                {
                    rows.Add((channel, name, kind, actual));
                }
            }
            return rows;
        }
        var answers = (await Task.WhenAll(actors.Select(actor => RunAsync(actor, deploy: false)))).Zip(actors).ToList();
        foreach (var actor in actors)
        {
            answers.Add((await RunAsync(actor, deploy: true), actor));
        }
        var failures = new List<string>();
        foreach (var (rows, actor) in answers)
        {
            foreach (var (channel, name, kind, actual) in rows)
            {
                var expected = Expected[actor.Name][kind];
                output.WriteLine($"{actor.Name,-18} {channel,-6} {name,-40} {actual}");
                if (actual != expected)
                {
                    failures.Add($"{actor.Name} · {channel} {name}: expected {expected}, got {actual}");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        // Nothing hidden in the space reads of anyone, over the cookie API and as MCP text (reads only: the actors go at once).
        await Task.WhenAll(actors.Where(x => !x.Anonymous && x.Name is not ("outsider" or "owner lost access" or "disabled account")).Select(LeaksNothingAsync));
        async Task LeaksNothingAsync(Actor actor)
        {
            var seen = new StringBuilder();
            if (actor.Cookie is { } cookie)
            {
                seen.Append(await cookie.GetStringAsync($"/api/projector/spaces/{space}/overview?filter=all"));
                seen.Append(await cookie.GetStringAsync($"/api/projector/spaces/{space}/waiting"));
                seen.Append(await cookie.GetStringAsync($"/api/projector/spaces/{space}/catalog"));
            }
            seen.Append(await actor.Rest!.GetStringAsync($"/api/v1/projector/spaces/{space}/overview?filter=all"));
            seen.Append(await actor.Rest!.GetStringAsync($"/api/v1/projector/spaces/{space}/resolve?" +
                string.Join("&", HiddenKeys.Append("portal.orders").Select(key => "keys=" + Uri.EscapeDataString(key)))));
            foreach (var (tool, arguments) in new (string, object)[] { ("projector_overview", new { spaceId = space, filter = "all" }), ("projector_waiting", new { spaceId = space }),
                         ("projector_get_catalog", new { spaceId = space }), ("projector_query", new { spaceId = space, limit = 200 }), ("projector_resolve", new { spaceId = space, keys = HiddenKeys.Append("portal.orders").ToArray() }) })
            {
                var result = await McpSite.CallAsync(actor.Mcp!, tool, arguments);
                seen.Append(McpSite.Text(result)).Append(result.StructuredContent?.GetRawText());
            }
            var text = seen.ToString();
            Assert.Contains("portal.orders", text);
            // Controls: the markers are visible where the rights allow them, so their absence elsewhere means something.
            if (actor.Name is "wiki admin")
            {
                Assert.Contains("TAJNA-U", text);
            }
            if (actor.Name is "wiki admin" or "space admin")
            {
                Assert.Contains("TAJNA-Q", text);
            }
            foreach (var marker in Markers(actor.Name))
            {
                Assert.False(text.Contains(marker, StringComparison.Ordinal), $"{actor.Name} sees {marker}");
            }
        }
    }

    /// <summary>The keys of what nobody but the page's editors may see; asked for by name, they come back only as unknown.</summary>
    private static readonly string[] HiddenKeys = ["hide.trash-cat", "hide.draft-cat", "hide.own-cat", "hide.other-cat"];

    /// <summary>What an actor must not find: trashed, template and other spaces' content for anyone; the wiki admin's unpublished pages
    /// for anyone else; pages with their own access for those without a role on them. Catalog keys asked for by name come back only in
    /// "not in the catalog" warnings, so their titles are the markers.</summary>
    private static IEnumerable<string> Markers(string actor)
    {
        string[] always = ["TAJNA-T", "TAJNA-M", "TAJNA-H", "TAJNY-KOS", "TAJNY-CIZI", "leak.trashed", "leak.template", "leak.hidden"];
        string[] unpublished = actor is "wiki admin" ? [] : ["TAJNA-U", "TAJNY-KONCEPT", "leak.unpublished"];
        string[] own = actor is "wiki admin" or "space admin" ? [] : ["TAJNA-Q", "TAJNY-VLASTNI", "leak.own"];
        return [.. always, .. unpublished, .. own];
    }

    private static async Task SeedHiddenAsync(McpSite f, string space, string hiddenSpace)
    {
        await McpSite.PageAsync(f.Admin, space, "Koncept", Json(Block("00000000-0000-4000-8000-0000000000d1", "leak.unpublished", "Koncept", state: "waiting_for_input", party: "TAJNA-U")), "", publish: false);
        var trashed = await McpSite.PageAsync(f.Admin, space, "Koš", Json(Block("00000000-0000-4000-8000-0000000000d2", "leak.trashed", "Koš", state: "waiting_for_input", party: "TAJNA-T")), "");
        var template = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/pages", new
        {
            spaceId = space, parentId = (string?)null, title = "Šablona", isTemplate = true,
            contentJson = Json(Block("00000000-0000-4000-8000-0000000000d3", "leak.template", "Šablona", state: "waiting_for_input", party: "TAJNA-M")), markdown = ""
        }), HttpStatusCode.OK);
        var own = await McpSite.PageAsync(f.Admin, space, "Vlastní", Json(Block("00000000-0000-4000-8000-0000000000d4", "leak.own", "Vlastní", state: "waiting_for_input", party: "TAJNA-Q")), "");
        await McpSite.PageAsync(f.Admin, hiddenSpace, "Cizí", Json(Block("00000000-0000-4000-8000-0000000000d5", "leak.hidden", "Cizí", state: "waiting_for_input", party: "TAJNA-H")), "");
        // Catalog pages: trashed, unpublished, with its own access, and in the other space.
        var trashedCatalog = await McpSite.PageAsync(f.Admin, space, "Katalog v koši", Json(Block("00000000-0000-4000-8000-0000000000e1", "hide.trash-cat", "TAJNY-KOS")), "");
        var draftCatalog = await McpSite.PageAsync(f.Admin, space, "Katalog koncept", Json(Block("00000000-0000-4000-8000-0000000000e2", "hide.draft-cat", "TAJNY-KONCEPT")), "", publish: false);
        var ownCatalog = await McpSite.PageAsync(f.Admin, space, "Katalog vlastní", Json(Block("00000000-0000-4000-8000-0000000000e3", "hide.own-cat", "TAJNY-VLASTNI")), "");
        var hiddenCatalog = await McpSite.PageAsync(f.Admin, hiddenSpace, "Katalog cizí", Json(Block("00000000-0000-4000-8000-0000000000e4", "hide.other-cat", "TAJNY-CIZI")), "");
        foreach (var id in new[] { trashedCatalog.Id, draftCatalog.Id, ownCatalog.Id, hiddenCatalog.Id })
        {
            await MarkCatalogAsync(f, id);
        }
        await f.Site.ExecuteAsync("""
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"=@admin,"TrashBatchId"=gen_random_uuid() WHERE "Id" IN (@a,@b)
            """, ("admin", Guid.Parse(f.AdminId)), ("a", Guid.Parse(trashed.Id)), ("b", Guid.Parse(trashedCatalog.Id)));
        await f.Site.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id" IN (@a,@b)""", ("a", Guid.Parse(own.Id)), ("b", Guid.Parse(ownCatalog.Id)));
        Assert.True(template["isTemplate"]!.GetValue<bool>());
    }

    private static async Task MarkCatalogAsync(McpSite f, string pageId)
        => await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Put, $"/api/projector/pages/{pageId}/catalog", new { isCatalog = true }), HttpStatusCode.OK);

    private static async Task<Actor> ActorAsync(McpSite f, string name, HttpClient cookie, string? userId)
    {
        var (_, rest) = await McpSite.IssueAsync(cookie, f.Site.Password, ["read", "write"], ["rest"]);
        var (_, mcp) = await McpSite.IssueAsync(cookie, f.Site.Password, ["read", "write"], ["mcp"]);
        return new Actor(name, cookie, ProjectorApiTests.Bearer(f, rest), await f.ClientAsync(mcp));
    }

    private static List<(string Channel, string Name, Kind Kind, Func<Actor, Task<string?>> Call)> Operations(string space, string page, string catalog)
    {
        var list = new List<(string, string, Kind, Func<Actor, Task<string?>>)>();
        void Web(string name, Kind kind, HttpMethod method, string path, object? body = null)
            => list.Add(("web", name, kind, actor => actor.Cookie is null ? Task.FromResult<string?>(null) : Http(actor.Cookie, method, path, body, csrf: true, kind)));
        void Rest(string name, Kind kind, HttpMethod method, string path, object? body = null)
            => list.Add(("v1", name, kind, actor => Http(actor.Rest!, method, path, body, csrf: false, kind)));
        void Tool(string name, Kind kind, object arguments)
            => list.Add(("mcp", name, kind, actor => actor.Anonymous ? AnonymousMcpAsync(actor.Rest!, name, arguments) : McpAsync(actor.Mcp!, name, arguments, kind)));

        var deployment = new { spaceId = space, title = "Kopie" };
        Web("GET catalog", Kind.SpaceRead, HttpMethod.Get, $"/api/projector/spaces/{space}/catalog");
        Web("PUT page catalog", Kind.PageWrite, HttpMethod.Put, $"/api/projector/pages/{page}/catalog", new { isCatalog = false });
        Web("GET page requirements", Kind.PageRead, HttpMethod.Get, $"/api/projector/pages/{page}/requirements");
        Web("GET overview", Kind.SpaceRead, HttpMethod.Get, $"/api/projector/spaces/{space}/overview?filter=all");
        Web("GET waiting", Kind.SpaceRead, HttpMethod.Get, $"/api/projector/spaces/{space}/waiting");
        Web("GET page definitions", Kind.PageRead, HttpMethod.Get, $"/api/projector/pages/{page}/definitions");
        Web("GET presets", Kind.SpaceRead, HttpMethod.Get, $"/api/projector/spaces/{space}/presets");
        Web("POST deployments", Kind.Deploy, HttpMethod.Post, $"/api/projector/pages/{page}/deployments", deployment);

        Rest("GET catalog", Kind.SpaceRead, HttpMethod.Get, $"/api/v1/projector/spaces/{space}/catalog");
        Rest("GET resolve", Kind.SpaceRead, HttpMethod.Get, $"/api/v1/projector/spaces/{space}/resolve?keys=portal.orders");
        Rest("GET overview", Kind.SpaceRead, HttpMethod.Get, $"/api/v1/projector/spaces/{space}/overview?filter=all");
        Rest("GET waiting", Kind.SpaceRead, HttpMethod.Get, $"/api/v1/projector/spaces/{space}/waiting");
        Rest("GET page requirements", Kind.PageRead, HttpMethod.Get, $"/api/v1/projector/pages/{page}/requirements");
        Rest("PUT page catalog", Kind.PageWrite, HttpMethod.Put, $"/api/v1/projector/pages/{page}/catalog", new { isCatalog = false });
        Rest("GET page definitions", Kind.PageRead, HttpMethod.Get, $"/api/v1/projector/pages/{page}/definitions");
        Rest("GET presets", Kind.SpaceRead, HttpMethod.Get, $"/api/v1/projector/spaces/{space}/presets");
        Rest("POST deployments", Kind.Deploy, HttpMethod.Post, $"/api/v1/projector/pages/{page}/deployments", deployment);
        Rest("PATCH requirement", Kind.PageWrite, HttpMethod.Patch, $"/api/v1/projector/pages/{page}/requirements/{B1}", new { fields = new { state = "done" }, expectedVersion = 999 });
        Rest("POST requirements", Kind.PageWrite, HttpMethod.Post, $"/api/v1/projector/pages/{page}/requirements", new { keys = new[] { "portal.orders" }, expectedVersion = 999 });
        Rest("POST requirements/new", Kind.PageWrite, HttpMethod.Post, $"/api/v1/projector/pages/{page}/requirements/new", new { fields = new { title = "Nový" }, expectedVersion = 999 });
        Rest("POST requirements/from-deployment", Kind.PageWrite, HttpMethod.Post, $"/api/v1/projector/pages/{page}/requirements/from-deployment",
            new { sourcePageId = catalog, expectedVersion = 999 });
        Rest("POST move", Kind.PageWrite, HttpMethod.Post, $"/api/v1/projector/pages/{page}/requirements/{B1}/move", new { position = new { }, expectedVersion = 999 });
        Rest("DELETE requirement", Kind.PageWrite, HttpMethod.Delete, $"/api/v1/projector/pages/{page}/requirements/{B1}?expectedVersion={Stale}");

        Tool("projector_get_requirements", Kind.PageRead, new { pageId = page });
        Tool("projector_get_catalog", Kind.SpaceRead, new { spaceId = space });
        Tool("projector_resolve", Kind.SpaceRead, new { spaceId = space, keys = new[] { "portal.orders" } });
        Tool("projector_overview", Kind.SpaceRead, new { spaceId = space, filter = "all" });
        Tool("projector_waiting", Kind.SpaceRead, new { spaceId = space });
        Tool("projector_query", Kind.SpaceRead, new { spaceId = space, states = new[] { "waiting_for_input" } });
        Tool("projector_list_presets", Kind.SpaceRead, new { spaceId = space });
        Tool("projector_create_requirement", Kind.PageWrite, new { pageId = page, expectedVersion = 999, title = "Nový" });
        Tool("projector_insert_from_deployment", Kind.PageWrite, new { pageId = page, sourcePageId = catalog, expectedVersion = 999 });
        Tool("projector_update_requirement", Kind.PageWrite, new { pageId = page, blockId = B1, expectedVersion = 999, state = "done" });
        Tool("projector_insert_requirements", Kind.PageWrite, new { pageId = page, keys = new[] { "portal.orders" }, expectedVersion = 999 });
        Tool("projector_move_requirement", Kind.PageWrite, new { pageId = page, blockId = B1, expectedVersion = 999 });
        Tool("projector_remove_requirement", Kind.PageWrite, new { pageId = page, blockId = B1, expectedVersion = 999 });
        Tool("projector_set_catalog_page", Kind.PageWrite, new { pageId = page, isCatalog = false });
        Tool("projector_create_deployment", Kind.Deploy, new { sourcePageId = page, spaceId = space, title = "Kopie" });
        return list;
    }

    private static async Task<string?> Http(HttpClient client, HttpMethod method, string path, object? body, bool csrf, Kind kind)
    {
        using var request = new HttpRequestMessage(method, path);
        if (csrf && method != HttpMethod.Get)
        {
            var token = await client.GetAsync("/api/auth/csrf");
            if (!token.IsSuccessStatusCode)
            {
                return Code((int)token.StatusCode, kind);
            }
            request.Headers.Add("X-CSRF-TOKEN", (await token.Content.ReadFromJsonAsync<JsonNode>())!["token"]!.GetValue<string>());
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return Code((int)(await client.SendAsync(request)).StatusCode, kind);
    }

    private static string Code(int status, Kind kind) => status switch
    {
        >= 200 and < 300 => "ok",
        409 when kind == Kind.PageWrite => "ok",
        _ => status.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private static async Task<string?> McpAsync(McpClient client, string tool, object arguments, Kind kind)
    {
        CallToolResult result;
        try
        {
            result = await McpSite.CallAsync(client, tool, arguments);
        }
        catch (Exception error) when (Status(error) is { } status)
        {
            return Code(status, kind);
        }
        if (result.IsError != true)
        {
            return "ok";
        }
        var structured = McpSite.Structured(result);
        return structured["code"]!.GetValue<string>() == "version_conflict" ? "ok" : Code(structured["status"]!.GetValue<int>(), kind);
    }

    /// <summary>The HTTP status of a refused MCP request (the SDK wraps the transport's exception).</summary>
    private static int? Status(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
        {
            if (error is HttpRequestException { StatusCode: { } status })
            {
                return (int)status;
            }
        }
        return null;
    }

    /// <summary>tools/call without a credential: refused before MCP.</summary>
    private static async Task<string?> AnonymousMcpAsync(HttpClient client, string tool, object arguments)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return ((int)(await client.SendAsync(request)).StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
