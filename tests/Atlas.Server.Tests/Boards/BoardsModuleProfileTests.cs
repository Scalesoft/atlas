using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Boards;
using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.Boards.Queries;
using Atlas.Server.Platform.Live;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Atlas.Server.Tests.Boards;

/// <summary>
/// The two profiles of the distribution in this prototype (Atlas 3 plan 5.1): a plain wiki and a wiki with boards. Switched off,
/// Boards has no route, no Wolverine handler (so none of its messages can run, not even through the bus), no service and no hub
/// channel, while its tables exist (every module's migrations always run). Switched on, all of it is there.
/// </summary>
public sealed class BoardsModuleProfileTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Without_the_module_a_wiki_has_no_board_route_handler_service_or_channel()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_profile_wiki_", settings => settings["Modules:Boards:Enabled"] = "false", shared: true);
        var cookies = new CookieContainer();
        using var admin = site.CreateDefaultClient(new Uri("https://localhost"), new CookieContainerHandler(cookies));
        await site.CompleteSetupAsync(admin);

        Assert.DoesNotContain(Routes(site), route => route.Contains("boards", StringComparison.Ordinal));
        Assert.Empty(BoardChains(site));
        Assert.Equal(["ai", "ai-admin", "jobs", "logs", "obj", "page", "tree", "user"], site.Services.GetServices<ILiveChannel>().Select(channel => channel.Kind).Order());
        using (var scope = site.Services.CreateScope())
        {
            Assert.Null(scope.ServiceProvider.GetService<BoardAccess>());
            // Not even an in-process caller reaches a board handler.
            await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<object>(new GetBoard(Guid.NewGuid())));
        }
        var modules = await AtlasHttpSite.Json(await admin.GetAsync("/api/modules"), HttpStatusCode.OK);
        Assert.Equal("""{"modules":[{"id":"pages","version":1}],"contentTypes":["atlas.page"]}""", modules.ToJsonString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/boards/{Guid.NewGuid()}")).StatusCode);

        await using var hub = BoardLiveHttpTests.Connect(site, cookies);
        await hub.StartAsync();
        var refused = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<LiveSubscribed>("Subscribe", BoardLiveChannel.Name(Guid.NewGuid())));
        Assert.EndsWith("unsupported", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1L, await site.ScalarAsync("""SELECT count(*) FROM pg_tables WHERE tablename='Board' AND schemaname=current_schema()"""));

        // No board views; the object path and the page's view session still work for pages.
        refused = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<ViewJoined>("JoinView", BoardLiveChannel.Name(Guid.NewGuid())));
        Assert.EndsWith("unsupported", refused.Message, StringComparison.Ordinal);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var page = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Wiki page", contentJson = rich, markdown = "" }), HttpStatusCode.OK))["id"]!.GetValue<string>();
        var read = await AtlasHttpSite.Json(await admin.GetAsync($"/api/objects/{page}"), HttpStatusCode.OK);
        Assert.Equal(("atlas.page", "yjsText", true), (read["typeId"]!.GetValue<string>(), read["editingModel"]!.GetValue<string>(), read["canEdit"]!.GetValue<bool>()));
        Assert.Equal(page, Assert.Single((await hub.InvokeAsync<ViewJoined>("JoinView", "page:" + page)).Members).ObjectId.ToString("D"));
    }

    [PostgreSqlFact]
    public async Task With_the_module_its_routes_handlers_services_and_channel_are_registered()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_profile_boards_", settings => settings["Modules:Boards:Enabled"] = "true", shared: true);
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);

        Assert.Equal(23, Routes(site).Count(route => route.Contains("/boards", StringComparison.Ordinal)));
        Assert.Contains(typeof(GetBoard), BoardChains(site));
        Assert.Equal(["ai", "ai-admin", "board", "jobs", "logs", "obj", "page", "tree", "user"], site.Services.GetServices<ILiveChannel>().Select(channel => channel.Kind).Order());
        var modules = await AtlasHttpSite.Json(await admin.GetAsync("/api/modules"), HttpStatusCode.OK);
        Assert.Equal("""{"modules":[{"id":"pages","version":1},{"id":"boards","version":1}],"contentTypes":["atlas.page","atlas.board"]}""", modules.ToJsonString());
    }

    /// <summary>
    /// Production loads the handler code generated at build time with every module on (Program, <c>codegen write</c>) and
    /// refuses to start if a handler it discovers has none (TypeLoadMode.Static, AssertAllPreGeneratedTypesExist; types are
    /// looked up by name). A wiki with Boards off finds every handler of its own there, with the code its own container makes:
    /// the switch only takes the board chains away. JasperFx orders fields, constructor parameters and service lookups
    /// differently even between two hosts of one configuration, so the code is compared as its tokens.
    /// </summary>
    [PostgreSqlFact]
    public async Task The_code_generated_with_every_module_on_holds_the_handler_code_of_a_wiki_without_boards()
    {
        await using var withBoards = await AtlasHttpSite.CreateAsync("atlas_codegen_boards_", settings => settings["Modules:Boards:Enabled"] = "true", shared: true);
        await using var wiki = await AtlasHttpSite.CreateAsync("atlas_codegen_wiki_", settings => settings["Modules:Boards:Enabled"] = "false", shared: true);
        var generated = GeneratedCode(withBoards);
        var needed = GeneratedCode(wiki);

        Assert.Empty(needed.Keys.Except(generated.Keys));
        Assert.All(needed, handler => Assert.Equal(generated[handler.Key], handler.Value));
        Assert.Equal(BoardChains(withBoards).Length, generated.Count - needed.Count);
    }

    /// <summary>The generated source of every handler as sorted tokens, by its generated type name, as this host's container
    /// makes it; compiled through the test process's shared compiler, so a source seen before costs nothing.</summary>
    private static Dictionary<string, string> GeneratedCode(AtlasHttpSite site)
    {
        var graph = ((WolverineRuntime)site.Services.GetRequiredService<IWolverineRuntime>()).Handlers;
        // Internal in Wolverine 5.28; a rename fails this test loudly, never silently.
        var source = typeof(HandlerChain).GetProperty("SourceCode", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return graph.Chains.ToDictionary(chain => chain.TypeName, chain =>
        {
            graph.HandlerFor(chain.MessageType);
            return string.Join(' ', Regex.Split((string)source.GetValue(chain)!, @"[\s,();{}]+").Where(token => token.Length > 0).Order(StringComparer.Ordinal));
        });
    }

    private static string[] Routes(AtlasHttpSite site)
    {
        return site.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText ?? "").ToArray();
    }

    private static Type[] BoardChains(AtlasHttpSite site)
    {
        return ((WolverineRuntime)site.Services.GetRequiredService<IWolverineRuntime>()).Handlers.Chains
            .Select(chain => chain.MessageType)
            .Where(type => type.Namespace?.StartsWith("Atlas.Server.Features.Boards", StringComparison.Ordinal) == true)
            .ToArray();
    }
}
