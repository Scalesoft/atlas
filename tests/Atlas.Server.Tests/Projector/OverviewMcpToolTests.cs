using System.Text.Json.Nodes;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>
/// The overview over MCP (plan 3.4 R6, tests/fixtures/content/overview-mcp.json): atlas_get_overview collects a page or a whole board as the
/// web's kinds do (each person and issue once, a board's product and service cards' requirements), projector_compose_board lays out the
/// page's deployment board as „Tabule nasazení“ and only adds what is missing, and an AI writes an overview as the dialect's ```a-overview.
/// </summary>
public sealed class OverviewMcpToolTests
{
    private const string B1 = "00000000-0000-4000-8000-0000000000b1", B2 = "00000000-0000-4000-8000-0000000000b2";

    private static JsonObject Issue(string key, string state) => new()
    {
        ["type"] = "youtrackIssue",
        ["attrs"] = new JsonObject { ["id"] = "id-" + key, ["key"] = key, ["title"] = "Úkol " + key, ["url"] = "https://yt.example.com/issue/" + key, ["state"] = state },
    };

    [PostgreSqlFact]
    public async Task Overview_of_a_page_and_a_board_and_the_composed_board()
    {
        await using var f = await McpSite.CreateAsync(settings: settings =>
        {
            foreach (var (key, value) in TestModules.AllEnabledSettings)
            {
                settings[key] = value;
            }
        });
        var space = await ProjectorMcpToolTests.FirstSpaceAsync(f);
        var nia = Block(B1, "nia.cert", "Certifikát NIA", state: "done", direction: "inbound");
        nia["attrs"]!["system"] = "NIA";
        var jana = new JsonObject { ["name"] = "Jana Nováková", ["organization"] = "Nemocnice", ["emails"] = new JsonArray("jana@example.cz") };
        var rich = Rich(Heading("Portál", "portal"), nia, Block(B2, "portal.app", "Aplikace"),
            new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(Issue("YT-1", "Open"), new JsonObject { ["type"] = "text", ["text"] = " a " }, Issue("YT-2", "Fixed"), Issue("yt-1", "Open")) },
            new JsonObject { ["type"] = "contactCard", ["attrs"] = new JsonObject { ["data"] = new JsonObject { ["version"] = 1, ["contacts"] = new JsonArray(jana) } } },
            new JsonObject { ["type"] = "overview", ["attrs"] = new JsonObject { ["kind"] = "contacts" } });
        foreach (var plugin in new[] { ("contacts", 2), ("youtrack", 1) })
        {
            rich["plugins"]!.AsArray().Add(new JsonObject { ["id"] = plugin.Item1, ["version"] = plugin.Item2 });
        }
        var page = await PageAsync(f.Admin, space, "Nasazení", rich.ToJsonString(), "");
        var agent = await f.ClientAsync(await f.AdminTokenAsync("read", "write"));

        // The page: each issue and person once, the summary as the readable line, the people as vCard too.
        var contacts = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_get_overview", new { kind = "contacts", pageId = page.Id })));
        Assert.Contains("FN:Jana Nováková", contacts["vcard"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Single(contacts["contacts"]!.AsArray());
        var issues = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_get_overview", new { kind = "issues", pageId = page.Id })));
        Assert.Equal("YT-1, YT-2", issues["keys"]!.GetValue<string>());
        Assert.Equal("Fixed", issues["issues"]![1]!["state"]!.GetValue<string>());
        var summary = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_get_overview", new { kind = "requirements", pageId = page.Id })));
        Assert.StartsWith("**Stav nasazení:** 50 % hotovo (1 z 2)", summary["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(agent, "atlas_get_overview", new { kind = "contacts" })));

        // „Tabule nasazení“: the product, its service and the connector, onto the empty board 3.5.0 left under the page in the tree; a
        // second run adds nothing to the same board.
        var empty = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_create_board", new { spaceId = space, title = "Tabule nasazení", parentPageId = page.Id })));
        var composed = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "projector_compose_board", new { pageId = page.Id })));
        Assert.Equal((empty["boardId"]!.GetValue<string>(), false), (composed["boardId"]!.GetValue<string>(), composed["created"]!.GetValue<bool>()));
        Assert.Equal(3, composed["added"]!.GetValue<int>());
        var again = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "projector_compose_board", new { pageId = page.Id })));
        Assert.Equal((composed["boardId"]!.GetValue<string>(), false, 0), (again["boardId"]!.GetValue<string>(), again["created"]!.GetValue<bool>(), again["added"]!.GetValue<int>()));
        var boardId = composed["boardId"]!.GetValue<string>();
        var board = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_get_board", new { boardId })));
        Assert.Equal(["connector", "page", "page"], board["items"]!.AsArray().Select(x => x!["kind"]!.GetValue<string>()).Order(StringComparer.Ordinal));

        // The board as one document: an overview block, an issue block, and the cards' requirements counted once.
        ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_edit_board", new
        {
            boardId,
            changes = new object[]
            {
                new { type = "create", item = new { kind = "block", x = 1000, y = 0, width = 400, height = 300, z = 10, props = new { block = new { type = "overview", attrs = new { kind = "issues" } } } } },
                new { type = "create", item = new { kind = "block", x = 1000, y = 400, width = 300, height = 40, z = 11, props = new { block = Issue("YT-9", "Open") } } },
            },
        }));
        var onBoard = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_get_overview", new { kind = "requirements", boardId })));
        Assert.StartsWith("**Stav nasazení:** 50 % hotovo (1 z 2)", onBoard["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("YT-9", Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_get_overview", new { kind = "issues", boardId })))["keys"]!.GetValue<string>());

        // An AI writes an overview as the fence; it reads back as the same overview, the contacts one as text.
        var read = ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_get_page", new { pageId = page.Id, version = "working" }));
        var working = Structured(read);
        Assert.Contains("```a-overview\nkind: contacts\n```", Text(read), StringComparison.Ordinal);
        ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_insert_section", new
        {
            pageId = page.Id, expectedVersion = working["expectedVersion"]!.GetValue<int>(), heading = "Úkoly", level = 2, markdown = "```a-overview\nkind: issues\n```",
        }));
        var written = Text(ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_get_page", new { pageId = page.Id, version = "working" })));
        Assert.Contains("## Úkoly\n\n```a-overview\nkind: issues\n```", written, StringComparison.Ordinal);
        Assert.Contains("```a-overview\nkind: contacts\n```", written, StringComparison.Ordinal);

        // A page without a board gets its own (the page decides who reads it, so its own access is no obstacle), never a tree board, and
        // a second run finds it again.
        var own = await PageAsync(f.Admin, space, "Nasazení 2", Rich(Heading("Portál", "portal"), nia.DeepClone(), Block(B2, "portal.app", "Aplikace")).ToJsonString(), "");
        await f.Site.ScalarAsync("""UPDATE "Page" SET "OwnAccess" = true WHERE "Id" = @page""", ("page", Guid.Parse(own.Id)));
        var made = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "projector_compose_board", new { pageId = own.Id })));
        Assert.Equal((true, 3), (made["created"]!.GetValue<bool>(), made["added"]!.GetValue<int>()));
        var madeId = Guid.Parse(made["boardId"]!.GetValue<string>());
        Assert.Equal("projector.board", await f.Site.ScalarAsync("""SELECT "Purpose" FROM "Board" WHERE "Id" = @board""", ("board", madeId)));
        var found = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "projector_compose_board", new { pageId = own.Id })));
        Assert.Equal((madeId.ToString(), false, 0), (found["boardId"]!.GetValue<string>(), found["created"]!.GetValue<bool>(), found["added"]!.GetValue<int>()));
    }

    /// <summary>projector_compose_board with arrange does the web's „Uspořádat jako architekturu“: the moved cards go back into the picture, a second call moves nothing.</summary>
    [PostgreSqlFact]
    public async Task Compose_with_arrange_puts_the_cards_into_the_architecture_picture()
    {
        await using var f = await McpSite.CreateAsync(settings: settings =>
        {
            foreach (var (key, value) in TestModules.AllEnabledSettings)
            {
                settings[key] = value;
            }
        });
        var space = await ProjectorMcpToolTests.FirstSpaceAsync(f);
        var nia = Block(B1, "nia.cert", "Certifikát NIA", state: "done", direction: "inbound");
        nia["attrs"]!["system"] = "NIA";
        var page = await PageAsync(f.Admin, space, "Nasazení", Rich(Heading("Portál", "portal"), nia, Block(B2, "portal.app", "Aplikace")).ToJsonString(), "");
        var agent = await f.ClientAsync(await f.AdminTokenAsync("read", "write"));
        var composed = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "projector_compose_board", new { pageId = page.Id })));
        var boardId = composed["boardId"]!.GetValue<string>();
        async Task<JsonNode> CardAsync(string display) => Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_get_board", new { boardId })))["items"]!.AsArray()
            .Single(x => x!["kind"]!.GetValue<string>() == "page" && x["props"]!["display"]!.GetValue<string>() == display)!;
        var product = await CardAsync("product");
        ProjectorMcpToolTests.Ok(await CallAsync(agent, "atlas_edit_board", new
        {
            boardId, changes = new object[] { new { type = "move", itemId = product["id"]!.GetValue<string>(), x = 900, y = 700, width = product["width"]!.GetValue<double>(), height = product["height"]!.GetValue<double>() } },
        }));

        var arranged = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "projector_compose_board", new { pageId = page.Id, arrange = true })));
        Assert.Equal((0, 2), (arranged["added"]!.GetValue<int>(), arranged["moved"]!.GetValue<int>()));
        product = await CardAsync("product");
        var service = await CardAsync("service");
        // The service stands centred under its product, a step of 160 below it: the picture compose makes.
        Assert.Equal(product["x"]!.GetValue<double>() + 50, service["x"]!.GetValue<double>());
        Assert.Equal(product["y"]!.GetValue<double>() + product["height"]!.GetValue<double>() + 160, service["y"]!.GetValue<double>());
        var again = Structured(ProjectorMcpToolTests.Ok(await CallAsync(agent, "projector_compose_board", new { pageId = page.Id, arrange = true })));
        Assert.Equal(0, again["moved"]!.GetValue<int>());
    }

    [PostgreSqlFact]
    public async Task Without_the_boards_module_the_compose_and_a_board_overview_say_so()
    {
        await using var f = await McpSite.CreateAsync(settings: settings =>
        {
            settings["Modules:Projector:Enabled"] = "true";
            settings["Modules:Boards:Enabled"] = "false";
        });
        var agent = await f.ClientAsync(await f.AdminTokenAsync("read", "write"));
        Assert.Equal("boards_disabled", ErrorCode(await CallAsync(agent, "projector_compose_board", new { pageId = Guid.NewGuid() })));
        Assert.Equal("boards_disabled", ErrorCode(await CallAsync(agent, "atlas_get_overview", new { kind = "requirements", boardId = Guid.NewGuid() })));
    }
}
